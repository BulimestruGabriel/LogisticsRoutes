using System.ComponentModel.DataAnnotations;
using System.Data;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.BusinessLayer.Services;

public class RemainingRoutePlanningService(LogisticsDbContext db, PlanningSettings settings)
{
    public async Task<PlanRemainingResponse> PlanAsync(PlanRemainingRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Day == default)
            throw new ValidationException("Alege o zi de livrare validă.");
        if (request.OrderIds is { } selected &&
            (selected.Count == 0 || selected.Any(id => id == Guid.Empty) ||
             selected.Distinct().Count() != selected.Count))
            throw new ValidationException("Selectează comenzi valide, fără identificatori repetați.");
        if (settings.DepotLatitude is not { } depotLatitude ||
            !double.IsFinite(depotLatitude) || depotLatitude is < -90 or > 90 ||
            settings.DepotLongitude is not { } depotLongitude ||
            !double.IsFinite(depotLongitude) || depotLongitude is < -180 or > 180)
            throw new PlanningConfigurationException(
                "Coordonatele depozitului nu sunt configurate corect (Planning:DepotLatitude și Planning:DepotLongitude).");

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            var ordersQuery = db.Orders.Where(order => order.DeliveryDate == request.Day &&
                order.Status == OrderStatus.Confirmed);
            if (request.OrderIds is { } selectedIds)
                ordersQuery = ordersQuery.Where(order => selectedIds.Contains(order.Id));
            var orders = await ordersQuery.ToListAsync(cancellationToken);
            if (request.OrderIds is { } requestedIds && orders.Count != requestedIds.Count)
                throw new PlanningConflictException("Comenzile selectate nu mai sunt confirmate în ziua aleasă. Reîncarcă datele.");
            if (orders.Count == 0)
                return new PlanRemainingResponse(request.Day, [], []);

            var orderIds = orders.Select(order => order.Id).ToList();
            if (await db.RouteStops.AnyAsync(stop => orderIds.Contains(stop.OrderId), cancellationToken))
                throw new PlanningConflictException("O comandă confirmată aparține deja unei rute. Reîncarcă datele.");

            var vehicleCapacities = await db.Vehicles.AsNoTracking().Select(vehicle => vehicle.Capacity)
                .ToListAsync(cancellationToken);
            decimal? maximum = vehicleCapacities.Count == 0 ? null : vehicleCapacities.Max();
            var tooLarge = orders.FirstOrDefault(order => maximum is null || order.Volume > maximum.Value);
            if (tooLarge is not null)
                throw new PlanningConflictException(maximum is null
                    ? "Nu există niciun vehicul în flotă. Adaugă un vehicul înainte de planificare."
                    : $"Comanda {tooLarge.Address} are volumul {tooLarge.Volume:0.###}, peste capacitatea maximă {maximum.Value:0.###}. Corectează comanda înainte de planificare.");

            var existingRoutes = await db.Routes.Where(route => route.Date == request.Day)
                .Include(route => route.Vehicle)
                .Include(route => route.Stops).ThenInclude(stop => stop.Order)
                .ToListAsync(cancellationToken);
            var usedVehicles = existingRoutes.Select(route => route.VehicleId).ToHashSet();
            var usedDrivers = existingRoutes.Select(route => route.DriverId).ToHashSet();
            var availableVehicles = await db.Vehicles.AsNoTracking()
                .Where(vehicle => !usedVehicles.Contains(vehicle.Id))
                .ToListAsync(cancellationToken);
            availableVehicles = availableVehicles.OrderBy(vehicle => vehicle.Capacity)
                .ThenBy(vehicle => vehicle.Id).ToList();
            var availableDrivers = await db.Drivers.AsNoTracking()
                .Where(driver => !usedDrivers.Contains(driver.Id))
                .OrderBy(driver => driver.Id).ToListAsync(cancellationToken);

            var existingLoads = existingRoutes
                .Where(route => route.Stops.Count > 0 &&
                    route.Stops.All(stop => stop.DeliveryStatus == DeliveryStatus.Pending) &&
                    route.Stops.Select(stop => stop.Order!.Zone).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 &&
                    route.Stops.Sum(stop => stop.Order!.Volume) <= route.Vehicle!.Capacity)
                .Select(route =>
                {
                    var load = new PlannedVehicleLoad(route.Vehicle!);
                    foreach (var stop in route.Stops.OrderBy(stop => stop.Sequence))
                        load.Add(stop.Order!);
                    return (Route: route, Load: load, OriginalCount: route.Stops.Count);
                }).ToList();
            var newLoads = availableVehicles.Select(vehicle => new PlannedVehicleLoad(vehicle)).ToList();
            var sortedOrders = orders.OrderByDescending(order => order.Volume)
                .ThenBy(order => order.Zone, StringComparer.OrdinalIgnoreCase)
                .ThenBy(order => order.Id).ToList();

            if (!Assign(0, 0))
                throw new PlanningConflictException(
                    "Nu există suficient spațiu pe rutele neîncepute și nici vehicule sau șoferi liberi pentru toate comenzile confirmate. Adaugă resurse ori corectează volumele și reîncearcă.");

            var extended = new List<ExtendedRouteResponse>();
            foreach (var (route, load, originalCount) in existingLoads)
            {
                var additions = load.Orders.Skip(originalCount).ToList();
                if (additions.Count == 0) continue;
                var last = route.Stops.OrderBy(stop => stop.Sequence).Last();
                var ordered = RoutePlanner.OrderStops(additions, last.Order!.Latitude,
                    last.Order.Longitude, cancellationToken);
                var nextSequence = last.Sequence;
                var addedStops = new List<PlannedStopResponse>();
                foreach (var order in ordered)
                {
                    nextSequence++;
                    db.RouteStops.Add(new RouteStop
                    {
                        Id = Guid.NewGuid(), RouteId = route.Id, OrderId = order.Id, Sequence = nextSequence
                    });
                    addedStops.Add(new PlannedStopResponse(order.Id, nextSequence, order.Address));
                }
                extended.Add(new ExtendedRouteResponse(route.Id, addedStops));
            }

            var created = new List<PlannedRouteResponse>();
            var occupiedNewLoads = newLoads.Where(load => load.Orders.Count > 0).ToList();
            for (var index = 0; index < occupiedNewLoads.Count; index++)
            {
                var load = occupiedNewLoads[index];
                var route = new Route
                {
                    Id = Guid.NewGuid(), Date = request.Day,
                    VehicleId = load.Vehicle.Id, DriverId = availableDrivers[index].Id
                };
                var stops = RoutePlanner.OrderStops(load.Orders, depotLatitude, depotLongitude,
                    cancellationToken);
                for (var stopIndex = 0; stopIndex < stops.Count; stopIndex++)
                    route.Stops.Add(new RouteStop
                    {
                        Id = Guid.NewGuid(), OrderId = stops[stopIndex].Id, Sequence = stopIndex + 1
                    });
                db.Routes.Add(route);
                created.Add(new PlannedRouteResponse(route.Id, load.Zone!, route.VehicleId, route.DriverId,
                    load.TotalVolume, stops.Select((order, stopIndex) =>
                        new PlannedStopResponse(order.Id, stopIndex + 1, order.Address)).ToList()));
            }

            foreach (var order in orders) order.Status = OrderStatus.Planned;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PlanRemainingResponse(request.Day, created, extended);

            bool Assign(int orderIndex, int openedNewRoutes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (orderIndex == sortedOrders.Count) return true;
                var order = sortedOrders[orderIndex];
                foreach (var (_, load, _) in existingLoads.Where(item =>
                             string.Equals(item.Load.Zone, order.Zone, StringComparison.OrdinalIgnoreCase) &&
                             item.Load.Vehicle.Capacity - item.Load.TotalVolume >= order.Volume)
                             .OrderBy(item => item.Load.Vehicle.Capacity - item.Load.TotalVolume - order.Volume))
                {
                    load.Add(order);
                    if (Assign(orderIndex + 1, openedNewRoutes)) return true;
                    load.Remove(order);
                }
                foreach (var load in newLoads.Where(load => load.Orders.Count > 0 &&
                             string.Equals(load.Zone, order.Zone, StringComparison.OrdinalIgnoreCase) &&
                             load.Vehicle.Capacity - load.TotalVolume >= order.Volume)
                             .OrderBy(load => load.Vehicle.Capacity - load.TotalVolume - order.Volume))
                {
                    load.Add(order);
                    if (Assign(orderIndex + 1, openedNewRoutes)) return true;
                    load.Remove(order);
                }
                if (openedNewRoutes >= availableDrivers.Count) return false;
                foreach (var load in newLoads.Where(load => load.Orders.Count == 0 &&
                             load.Vehicle.Capacity >= order.Volume))
                {
                    load.Add(order);
                    if (Assign(orderIndex + 1, openedNewRoutes + 1)) return true;
                    load.Remove(order);
                }
                return false;
            }
        }
        catch (Exception exception) when (DatabaseConflict.IsConcurrent(exception))
        {
            throw new PlanningConflictException("Planificarea s-a schimbat între timp. Reîncarcă datele și reîncearcă.");
        }
    }
}
