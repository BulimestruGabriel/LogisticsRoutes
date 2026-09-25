using System.ComponentModel.DataAnnotations;
using System.Data;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.BusinessLayer.Services;

public class RoutePlanningService(LogisticsDbContext db, PlanningSettings settings)
{
    public async Task<PlanRoutesResponse> PlanAsync(PlanRoutesRequest request, CancellationToken cancellationToken)
    {
        if (request.Day == default)
            throw new ValidationException("Day is required.");
        if (settings.DepotLatitude is not { } depotLatitude ||
            !double.IsFinite(depotLatitude) || depotLatitude is < -90 or > 90 ||
            settings.DepotLongitude is not { } depotLongitude ||
            !double.IsFinite(depotLongitude) || depotLongitude is < -180 or > 180)
            throw new PlanningConfigurationException(
                "Planning:DepotLatitude and Planning:DepotLongitude must be configured with valid coordinates.");

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            if (await db.Routes.AnyAsync(route => route.Date == request.Day, cancellationToken))
                throw new PlanningConflictException("Routes already exist for the requested day.");

            var orders = await db.Orders.Where(order => order.DeliveryDate == request.Day &&
                    order.Status == OrderStatus.Confirmed)
                .ToListAsync(cancellationToken);
            if (orders.Count == 0)
                return new PlanRoutesResponse(request.Day, []);

            var vehicles = await db.Vehicles.AsNoTracking().ToListAsync(cancellationToken);
            var drivers = await db.Drivers.AsNoTracking().OrderBy(driver => driver.Id)
                .ToListAsync(cancellationToken);
            var loads = RoutePlanner.Assign(orders, vehicles, drivers.Count, cancellationToken);
            var routes = new List<PlannedRouteResponse>(loads.Count);

            for (var index = 0; index < loads.Count; index++)
            {
                var load = loads[index];
                var route = new Route
                {
                    Id = Guid.NewGuid(),
                    Date = request.Day,
                    VehicleId = load.Vehicle.Id,
                    DriverId = drivers[index].Id
                };
                var stops = RoutePlanner.OrderStops(load.Orders, depotLatitude, depotLongitude,
                    cancellationToken);
                for (var stopIndex = 0; stopIndex < stops.Count; stopIndex++)
                {
                    route.Stops.Add(new RouteStop
                    {
                        Id = Guid.NewGuid(),
                        OrderId = stops[stopIndex].Id,
                        Sequence = stopIndex + 1
                    });
                }

                db.Routes.Add(route);
                routes.Add(new PlannedRouteResponse(route.Id, load.Zone!, route.VehicleId, route.DriverId,
                    load.TotalVolume,
                    stops.Select((order, stopIndex) =>
                        new PlannedStopResponse(order.Id, stopIndex + 1, order.Address)).ToList()));
            }

            foreach (var order in orders)
                order.Status = OrderStatus.Planned;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PlanRoutesResponse(request.Day, routes);
        }
        catch (Exception exception) when (DatabaseConflict.IsConcurrent(exception))
        {
            throw new PlanningConflictException("Planificarea s-a schimbat între timp. Reîncarcă datele și reîncearcă.");
        }
    }
}
