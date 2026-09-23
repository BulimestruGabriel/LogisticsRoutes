using System.ComponentModel.DataAnnotations;
using System.Data;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LogisticsRoutes.BusinessLayer.Services;

public class RouteStopStatusService(LogisticsDbContext db)
{
    public async Task<UpdateRouteStopStatusResponse?> UpdateAsync(Guid routeId, Guid stopId,
        UpdateRouteStopStatusRequest request, CancellationToken cancellationToken)
    {
        var statusName = request.Status?.Trim();
        if (!Enum.GetNames<DeliveryStatus>().Any(name =>
                string.Equals(name, statusName, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException(
                "Status necunoscut. Valori permise: Pending, Departed, Arrived, Delivered, Refused, PartialReturn.");

        var nextStatus = Enum.Parse<DeliveryStatus>(statusName!, true);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            var stop = await db.RouteStops.Include(item => item.Order)
                .SingleOrDefaultAsync(item => item.Id == stopId && item.RouteId == routeId, cancellationToken);
            if (stop is null)
                return null;

            DeliveryStatus[] allowed = stop.DeliveryStatus switch
            {
                DeliveryStatus.Pending => [DeliveryStatus.Departed],
                DeliveryStatus.Departed => [DeliveryStatus.Arrived],
                DeliveryStatus.Arrived => [DeliveryStatus.Delivered, DeliveryStatus.Refused,
                    DeliveryStatus.PartialReturn],
                _ => []
            };
            if (!allowed.Contains(nextStatus))
            {
                var choices = allowed.Length == 0 ? "nicio altă stare (stare finală)" : string.Join(", ", allowed);
                throw new RouteStopStatusConflictException(
                    $"Tranziție nepermisă: {stop.DeliveryStatus} → {nextStatus}. Din {stop.DeliveryStatus} sunt permise: {choices}.");
            }

            stop.DeliveryStatus = nextStatus;
            if (nextStatus == DeliveryStatus.Delivered)
                stop.Order!.Status = OrderStatus.Delivered;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new UpdateRouteStopStatusResponse(stop.Id, stop.DeliveryStatus.ToString(),
                stop.Order!.Status.ToString());
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            throw new RouteStopStatusConflictException(
                "Starea opririi a fost modificată între timp. Reîncarcă datele și încearcă din nou.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.SerializationFailure })
        {
            throw new RouteStopStatusConflictException(
                "Starea opririi a fost modificată între timp. Reîncarcă datele și încearcă din nou.");
        }
    }
}
