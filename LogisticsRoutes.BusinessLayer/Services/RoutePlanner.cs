using LogisticsRoutes.Domain.Entities;

namespace LogisticsRoutes.BusinessLayer.Services;

// Builds complete assignments in memory before any entities are written.
public static class RoutePlanner
{
    public static IReadOnlyList<PlannedVehicleLoad> Assign(
        IReadOnlyList<Order> orders,
        IReadOnlyList<Vehicle> vehicles,
        int driverCount,
        CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
            return [];

        var sortedVehicles = vehicles.OrderBy(vehicle => vehicle.Capacity).ThenBy(vehicle => vehicle.Id).ToList();
        if (sortedVehicles.Count == 0)
            throw new PlanningConflictException("No vehicles are available for planning.");
        if (orders.Any(order => order.Volume > sortedVehicles[^1].Capacity))
            throw new PlanningConflictException("At least one order exceeds the capacity of every vehicle.");

        var sortedOrders = orders.OrderByDescending(order => order.Volume)
            .ThenBy(order => order.Zone, StringComparer.OrdinalIgnoreCase)
            .ThenBy(order => order.Id)
            .ToList();
        var loads = sortedVehicles.Select(vehicle => new PlannedVehicleLoad(vehicle)).ToList();

        if (!TryAssign(0, 0))
            throw new PlanningConflictException("Not enough vehicles or drivers to plan all confirmed orders by zone and capacity.");

        return loads.Where(load => load.Orders.Count > 0).ToList();

        bool TryAssign(int orderIndex, int routeCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (orderIndex == sortedOrders.Count)
                return true;

            var order = sortedOrders[orderIndex];
            // Fill existing routes first, then try unused vehicles from smallest to largest.
            foreach (var load in loads.Where(load => load.Orders.Count > 0 &&
                         string.Equals(load.Zone, order.Zone, StringComparison.OrdinalIgnoreCase) &&
                         load.Vehicle.Capacity - load.TotalVolume >= order.Volume)
                         .OrderBy(load => load.Vehicle.Capacity - load.TotalVolume - order.Volume))
            {
                load.Add(order);
                if (TryAssign(orderIndex + 1, routeCount))
                    return true;
                load.Remove(order);
            }

            if (routeCount >= driverCount)
                return false;

            decimal? lastTriedCapacity = null;
            foreach (var load in loads.Where(load => load.Orders.Count == 0 &&
                         load.Vehicle.Capacity >= order.Volume))
            {
                if (load.Vehicle.Capacity == lastTriedCapacity)
                    continue;
                lastTriedCapacity = load.Vehicle.Capacity;
                load.Add(order);
                if (TryAssign(orderIndex + 1, routeCount + 1))
                    return true;
                load.Remove(order);
            }

            return false;
        }
    }

    public static IReadOnlyList<Order> OrderStops(
        IEnumerable<Order> orders, double depotLatitude, double depotLongitude,
        CancellationToken cancellationToken)
    {
        var remaining = orders.ToList();
        var result = new List<Order>(remaining.Count);
        var latitude = depotLatitude;
        var longitude = depotLongitude;

        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = remaining.OrderBy(order => HaversineKilometres(latitude, longitude,
                    order.Latitude, order.Longitude))
                .ThenBy(order => order.Id)
                .First();
            result.Add(next);
            remaining.Remove(next);
            latitude = next.Latitude;
            longitude = next.Longitude;
        }

        return result;
    }

    private static double HaversineKilometres(double latitude1, double longitude1,
        double latitude2, double longitude2)
    {
        const double earthRadiusKilometres = 6371.0088;
        const double radiansPerDegree = Math.PI / 180;
        var latitudeDelta = (latitude2 - latitude1) * radiansPerDegree;
        var longitudeDelta = (longitude2 - longitude1) * radiansPerDegree;
        var halfChord = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                        Math.Cos(latitude1 * radiansPerDegree) *
                        Math.Cos(latitude2 * radiansPerDegree) *
                        Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return 2 * earthRadiusKilometres * Math.Asin(Math.Sqrt(Math.Min(1, halfChord)));
    }
}

public class PlannedVehicleLoad(Vehicle vehicle)
{
    public Vehicle Vehicle { get; } = vehicle;
    public string? Zone { get; private set; }
    public decimal TotalVolume { get; private set; }
    public List<Order> Orders { get; } = [];

    internal void Add(Order order)
    {
        Zone ??= order.Zone;
        Orders.Add(order);
        TotalVolume += order.Volume;
    }

    internal void Remove(Order order)
    {
        Orders.RemoveAt(Orders.Count - 1);
        TotalVolume -= order.Volume;
        if (Orders.Count == 0)
            Zone = null;
    }
}
