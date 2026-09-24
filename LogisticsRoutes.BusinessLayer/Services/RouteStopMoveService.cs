using System.ComponentModel.DataAnnotations;
using System.Data;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LogisticsRoutes.BusinessLayer.Services;

public class RouteStopMoveService(LogisticsDbContext db)
{
    public async Task<bool> MoveAsync(Guid sourceRouteId, Guid stopId, MoveRouteStopRequest request,
        CancellationToken cancellationToken)
    {
        var destinationRouteId = request.DestinationRouteId;
        if (sourceRouteId == Guid.Empty || stopId == Guid.Empty || destinationRouteId == Guid.Empty ||
            sourceRouteId == destinationRouteId)
            throw new ValidationException("Sunt necesare ID-uri valide și două rute diferite.");

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            var routes = await db.Routes.AsNoTracking()
                .Include(route => route.Vehicle)
                .Include(route => route.Stops).ThenInclude(stop => stop.Order)
                .Where(route => route.Id == sourceRouteId || route.Id == destinationRouteId)
                .ToListAsync(cancellationToken);
            var source = routes.SingleOrDefault(route => route.Id == sourceRouteId);
            var destination = routes.SingleOrDefault(route => route.Id == destinationRouteId);
            var moved = source?.Stops.SingleOrDefault(stop => stop.Id == stopId);
            if (source is null || destination is null || moved is null)
                return false;

            if (source.Date != destination.Date)
                throw new RouteOrderConflictException("Rutele trebuie să fie în aceeași zi.");
            if (destination.Stops.Count == 0)
                throw new RouteOrderConflictException("Ruta destinație nu are o zonă stabilită.");
            if (source.Stops.Count == 1)
                throw new RouteOrderConflictException("Ultima oprire nu poate fi mutată: ruta sursă ar rămâne goală.");
            if (source.Stops.Concat(destination.Stops)
                .Any(stop => stop.DeliveryStatus != DeliveryStatus.Pending))
                throw new RouteOrderConflictException("Toate opririle din ambele rute trebuie să fie Pending.");
            if (source.Stops.Concat(destination.Stops).Any(stop =>
                    !string.Equals(stop.Order!.Zone, moved.Order!.Zone, StringComparison.OrdinalIgnoreCase)))
                throw new RouteOrderConflictException("Rutele trebuie să fie în aceeași zonă.");
            if (destination.Stops.Sum(stop => stop.Order!.Volume) + moved.Order!.Volume >
                destination.Vehicle!.Capacity)
                throw new RouteOrderConflictException("Capacitatea vehiculului destinație ar fi depășită.");

            var remaining = source.Stops.Where(stop => stop.Id != stopId)
                .OrderBy(stop => stop.Sequence).ToList();
            var destinationStops = destination.Stops.OrderBy(stop => stop.Sequence).ToList();

            // Free both sets of positive positions before writing the new order. RouteId stays
            // unchanged until the moved stop receives its final, unoccupied destination position.
            foreach (var routeId in new[] { sourceRouteId, destinationRouteId })
                await db.RouteStops.Where(stop => stop.RouteId == routeId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(stop => stop.Sequence, stop => -stop.Sequence),
                        cancellationToken);

            for (var index = 0; index < remaining.Count; index++)
            {
                var currentId = remaining[index].Id;
                var sequence = index + 1;
                await db.RouteStops.Where(stop => stop.Id == currentId && stop.RouteId == sourceRouteId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(stop => stop.Sequence, sequence),
                        cancellationToken);
            }

            for (var index = 0; index < destinationStops.Count; index++)
            {
                var currentId = destinationStops[index].Id;
                var sequence = index + 1;
                await db.RouteStops.Where(stop => stop.Id == currentId && stop.RouteId == destinationRouteId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(stop => stop.Sequence, sequence),
                        cancellationToken);
            }

            await db.RouteStops.Where(stop => stop.Id == stopId && stop.RouteId == sourceRouteId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(stop => stop.RouteId, destinationRouteId)
                    .SetProperty(stop => stop.Sequence, destinationStops.Count + 1), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (PostgresException exception) when (IsConcurrentConflict(exception))
        {
            throw new RouteOrderConflictException("Rutele au fost modificate între timp. Reîncarcă datele și încearcă din nou.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres &&
                                                  IsConcurrentConflict(postgres))
        {
            throw new RouteOrderConflictException("Rutele au fost modificate între timp. Reîncarcă datele și încearcă din nou.");
        }
    }

    private static bool IsConcurrentConflict(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.ForeignKeyViolation;
}
