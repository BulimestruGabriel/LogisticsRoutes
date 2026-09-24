using System.ComponentModel.DataAnnotations;
using System.Data;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LogisticsRoutes.BusinessLayer.Services;

public class RouteStopOrderService(LogisticsDbContext db)
{
    public async Task<RouteResponse?> ReorderAsync(Guid routeId, ReorderRouteStopsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            var route = await db.Routes.AsNoTracking().Include(item => item.Stops)
                .SingleOrDefaultAsync(item => item.Id == routeId, cancellationToken);
            if (route is null)
                return null;

            var stopIds = request.StopIds;
            if (stopIds is null || stopIds.Count != route.Stops.Count ||
                stopIds.Distinct().Count() != stopIds.Count ||
                !stopIds.ToHashSet().SetEquals(route.Stops.Select(stop => stop.Id)))
                throw new ValidationException("Lista trebuie să conțină exact o dată fiecare oprire a rutei.");

            if (route.Stops.Any(stop => stop.DeliveryStatus != DeliveryStatus.Pending))
                throw new RouteOrderConflictException("Ruta are deja o oprire începută; ordinea nu mai poate fi schimbată.");

            // Vacate the positive positions first: the unique (RouteId, Sequence) index is immediate.
            await db.RouteStops.Where(stop => stop.RouteId == routeId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(stop => stop.Sequence, stop => -stop.Sequence),
                    cancellationToken);

            for (var index = 0; index < stopIds.Count; index++)
            {
                var stopId = stopIds[index];
                var sequence = index + 1;
                await db.RouteStops.Where(stop => stop.Id == stopId && stop.RouteId == routeId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(stop => stop.Sequence, sequence),
                        cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return await new RouteQueryService(db).GetByIdAsync(routeId, cancellationToken);
        }
        catch (PostgresException exception) when (IsConcurrentConflict(exception))
        {
            throw new RouteOrderConflictException("Ruta a fost modificată între timp. Reîncarcă datele și încearcă din nou.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres &&
                                                  IsConcurrentConflict(postgres))
        {
            throw new RouteOrderConflictException("Ruta a fost modificată între timp. Reîncarcă datele și încearcă din nou.");
        }
    }

    private static bool IsConcurrentConflict(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation;
}
