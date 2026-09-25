using System.ComponentModel.DataAnnotations;
using System.Data;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.BusinessLayer.Services;

public class RouteOrderService(LogisticsDbContext db, PlanningSettings settings)
{
    public async Task<RouteResponse?> AddAsync(Guid routeId, AddRouteOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (routeId == Guid.Empty || request.OrderId == Guid.Empty)
            throw new ValidationException("ID-ul rutei și ID-ul comenzii sunt obligatorii.");
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            var route = await db.Routes.Include(item => item.Vehicle)
                .Include(item => item.Stops).ThenInclude(stop => stop.Order)
                .SingleOrDefaultAsync(item => item.Id == routeId, cancellationToken);
            if (route is null)
                return null;

            var order = await db.Orders.SingleOrDefaultAsync(item => item.Id == request.OrderId,
                cancellationToken);
            if (order is null)
                return null;

            if (settings.DepotLatitude is not { } depotLatitude ||
                !double.IsFinite(depotLatitude) || depotLatitude is < -90 or > 90 ||
                settings.DepotLongitude is not { } depotLongitude ||
                !double.IsFinite(depotLongitude) || depotLongitude is < -180 or > 180)
                throw new PlanningConfigurationException(
                    "Planning:DepotLatitude and Planning:DepotLongitude must be configured with valid coordinates.");

            if (await db.RouteStops.AnyAsync(stop => stop.OrderId == order.Id, cancellationToken))
                throw new RouteOrderConflictException("Comanda aparține deja unei rute.");
            if (order.Status != OrderStatus.Confirmed)
                throw new RouteOrderConflictException("Doar o comandă confirmată poate fi adăugată la rută.");
            if (order.DeliveryDate != route.Date)
                throw new RouteOrderConflictException("Comanda și ruta trebuie să aibă aceeași zi.");
            if (route.Stops.Count == 0 || route.Stops.Any(stop =>
                    !string.Equals(stop.Order!.Zone, order.Zone, StringComparison.OrdinalIgnoreCase)))
                throw new RouteOrderConflictException("Comanda și ruta trebuie să fie în aceeași zonă.");
            if (route.Stops.Any(stop => stop.DeliveryStatus != DeliveryStatus.Pending))
                throw new RouteOrderConflictException("Ruta are deja o oprire începută; ordinea nu mai poate fi schimbată.");
            if (route.Stops.Sum(stop => stop.Order!.Volume) + order.Volume > route.Vehicle!.Capacity)
                throw new RouteOrderConflictException("Capacitatea vehiculului ar fi depășită.");

            var ordered = RoutePlanner.OrderStops(route.Stops.Select(stop => stop.Order!).Append(order),
                depotLatitude, depotLongitude, cancellationToken);

            // The unique (RouteId, Sequence) index is immediate. Vacate all positive positions
            // before assigning the new order, so swaps never collide during SaveChanges.
            await db.RouteStops.Where(stop => stop.RouteId == route.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(stop => stop.Sequence, stop => -stop.Sequence),
                    cancellationToken);

            var stopsByOrder = route.Stops.ToDictionary(stop => stop.OrderId);
            for (var index = 0; index < ordered.Count; index++)
            {
                var currentOrder = ordered[index];
                if (stopsByOrder.TryGetValue(currentOrder.Id, out var stop))
                {
                    stop.Sequence = index + 1;
                    // ExecuteUpdate bypasses the change tracker, including when a stop keeps its old position.
                    db.Entry(stop).Property(item => item.Sequence).IsModified = true;
                }
                else
                    db.RouteStops.Add(new RouteStop
                    {
                        Id = Guid.NewGuid(), RouteId = route.Id, OrderId = currentOrder.Id,
                        Sequence = index + 1
                    });
            }

            order.Status = OrderStatus.Planned;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await new RouteQueryService(db).GetByIdAsync(route.Id, cancellationToken);
        }
        catch (Exception exception) when (DatabaseConflict.IsConcurrent(exception))
        {
            throw new RouteOrderConflictException("Ruta sau comanda a fost modificată între timp. Reîncarcă datele și încearcă din nou.");
        }
    }
}
