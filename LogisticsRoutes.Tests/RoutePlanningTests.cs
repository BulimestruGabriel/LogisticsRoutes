using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.BusinessLayer.Services;
using LogisticsRoutes.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.Tests;

public class RoutePlanningTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);
    private static readonly PlanningSettings Depot = new(0, 0);

    [Fact]
    public async Task SplitsZoneByCapacityAndPlansOnlyConfirmedOrdersForRequestedDay()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var orders = new[]
        {
            NewOrder("North", 6, 0, 1),
            NewOrder("North", 6, 0, 2),
            NewOrder("North", 4, 0, 3)
        };
        var newOrder = NewOrder("North", 1, 0, 4, OrderStatus.New);
        var otherDay = NewOrder("North", 1, 0, 5);
        otherDay.DeliveryDate = Day.AddDays(1);
        db.Orders.AddRange(orders.Append(newOrder).Append(otherDay));
        db.Vehicles.AddRange(NewVehicle(10), NewVehicle(6));
        db.Drivers.AddRange(NewDriver(), NewDriver());
        await db.SaveChangesAsync();

        var result = await new RoutePlanningService(db, Depot)
            .PlanAsync(new PlanRoutesRequest(Day), CancellationToken.None);

        Assert.Equal(2, result.Routes.Count);
        Assert.All(result.Routes, route => Assert.Equal("North", route.Zone));
        Assert.Equal(16, result.Routes.Sum(route => route.TotalVolume));
        Assert.Equal(2, result.Routes.Select(route => route.VehicleId).Distinct().Count());
        Assert.Equal(2, result.Routes.Select(route => route.DriverId).Distinct().Count());
        Assert.Equal(orders.Select(order => order.Id).Order(),
            result.Routes.SelectMany(route => route.Stops).Select(stop => stop.OrderId).Order());

        db.ChangeTracker.Clear();
        var routes = await db.Routes.Include(route => route.Stops).ToListAsync();
        var capacities = await db.Vehicles.ToDictionaryAsync(vehicle => vehicle.Id, vehicle => vehicle.Capacity);
        var volumes = await db.Orders.ToDictionaryAsync(order => order.Id, order => order.Volume);
        Assert.Equal(2, routes.Count);
        foreach (var route in routes)
        {
            Assert.True(route.Stops.Sum(stop => volumes[stop.OrderId]) <= capacities[route.VehicleId]);
            Assert.Equal(Enumerable.Range(1, route.Stops.Count), route.Stops.Select(stop => stop.Sequence).Order());
            Assert.All(route.Stops, stop => Assert.Null(stop.EstimatedArrival));
        }

        Assert.All(await db.Orders.Where(order => orders.Select(o => o.Id).Contains(order.Id)).ToListAsync(),
            order => Assert.Equal(OrderStatus.Planned, order.Status));
        Assert.Equal(OrderStatus.New, (await db.Orders.SingleAsync(order => order.Id == newOrder.Id)).Status);
        Assert.Equal(OrderStatus.Confirmed, (await db.Orders.SingleAsync(order => order.Id == otherDay.Id)).Status);
    }

    [Fact]
    public async Task StopsFollowNearestNeighborFromDepotAndEachPreviousStop()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var near = NewOrder("North", 1, 1, 0);
        var nextFromNear = NewOrder("North", 1, 2, 0);
        var closerToDepot = NewOrder("North", 1, 0, 1.1);
        db.Orders.AddRange(closerToDepot, near, nextFromNear);
        db.Vehicles.Add(NewVehicle(3));
        db.Drivers.Add(NewDriver());
        await db.SaveChangesAsync();

        await new RoutePlanningService(db, Depot).PlanAsync(new PlanRoutesRequest(Day), CancellationToken.None);

        db.ChangeTracker.Clear();
        var stops = await db.RouteStops.OrderBy(stop => stop.Sequence).ToListAsync();
        Assert.Equal(new[] { near.Id, nextFromNear.Id, closerToDepot.Id },
            stops.Select(stop => stop.OrderId));
        Assert.Equal(new[] { 1, 2, 3 }, stops.Select(stop => stop.Sequence));
    }

    [Fact]
    public async Task RoutesKeepZonesSeparateAndUseDistinctResources()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var north = NewOrder("North", 1, 1, 0);
        var south = NewOrder("South", 1, -1, 0);
        db.Orders.AddRange(north, south);
        db.Vehicles.AddRange(NewVehicle(2), NewVehicle(2));
        db.Drivers.AddRange(NewDriver(), NewDriver());
        await db.SaveChangesAsync();

        var result = await new RoutePlanningService(db, Depot)
            .PlanAsync(new PlanRoutesRequest(Day), CancellationToken.None);

        Assert.Equal(2, result.Routes.Count);
        Assert.Equal(new[] { "North", "South" }, result.Routes.Select(route => route.Zone).Order());
        Assert.All(result.Routes, route => Assert.Single(route.Stops));
        Assert.Equal(2, result.Routes.Select(route => route.VehicleId).Distinct().Count());
        Assert.Equal(2, result.Routes.Select(route => route.DriverId).Distinct().Count());
        Assert.Contains(result.Routes, route => route.Zone == "North" && route.Stops[0].OrderId == north.Id);
        Assert.Contains(result.Routes, route => route.Zone == "South" && route.Stops[0].OrderId == south.Id);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task InsufficientVehiclesOrDriversReturnsConflictWithoutChanges(
        int vehicleCount, int driverCount)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        db.Orders.AddRange(NewOrder("North", 1, 0, 1), NewOrder("South", 1, 0, 2));
        db.Vehicles.AddRange(Enumerable.Range(0, vehicleCount).Select(_ => NewVehicle(10)));
        db.Drivers.AddRange(Enumerable.Range(0, driverCount).Select(_ => NewDriver()));
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PlanningConflictException>(() =>
            new RoutePlanningService(db, Depot).PlanAsync(new PlanRoutesRequest(Day), CancellationToken.None));

        Assert.Contains("vehicles or drivers", exception.Message);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Routes.ToListAsync());
        Assert.Empty(await db.RouteStops.ToListAsync());
        Assert.All(await db.Orders.ToListAsync(), order => Assert.Equal(OrderStatus.Confirmed, order.Status));
    }

    [Fact]
    public async Task OversizedOrderReturnsConflictWithoutChanges()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        db.Orders.Add(NewOrder("North", 11, 0, 1));
        db.Vehicles.Add(NewVehicle(10));
        db.Drivers.Add(NewDriver());
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<PlanningConflictException>(() =>
            new RoutePlanningService(db, Depot).PlanAsync(new PlanRoutesRequest(Day), CancellationToken.None));

        Assert.Contains("exceeds", exception.Message);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Routes.ToListAsync());
        Assert.Empty(await db.RouteStops.ToListAsync());
        Assert.Equal(OrderStatus.Confirmed, (await db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task ExistingRouteReturnsConflictWithoutPlanningRemainingOrders()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var vehicle = NewVehicle(10);
        var driver = NewDriver();
        db.Vehicles.Add(vehicle);
        db.Drivers.Add(driver);
        db.Routes.Add(new Route { Id = Guid.NewGuid(), Date = Day, VehicleId = vehicle.Id, DriverId = driver.Id });
        db.Orders.Add(NewOrder("North", 1, 0, 1));
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<PlanningConflictException>(() =>
            new RoutePlanningService(db, Depot).PlanAsync(new PlanRoutesRequest(Day), CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Routes.CountAsync());
        Assert.Empty(await db.RouteStops.ToListAsync());
        Assert.Equal(OrderStatus.Confirmed, (await db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task DatabaseFailureRollsBackRoutesStopsAndStatusChanges()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        db.Orders.Add(NewOrder("North", 1, 0, 1));
        db.Vehicles.Add(NewVehicle(10));
        db.Drivers.Add(NewDriver());
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_order_status_update
            BEFORE UPDATE OF Status ON Orders
            BEGIN SELECT RAISE(ABORT, 'simulated database failure'); END;
            """);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            new RoutePlanningService(db, Depot).PlanAsync(new PlanRoutesRequest(Day), CancellationToken.None));
        Assert.Contains("simulated database failure", exception.ToString());

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Routes.ToListAsync());
        Assert.Empty(await db.RouteStops.ToListAsync());
        Assert.Equal(OrderStatus.Confirmed, (await db.Orders.SingleAsync()).Status);
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static LogisticsDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<LogisticsDbContext>().UseSqlite(connection).Options);

    private static Order NewOrder(string zone, decimal volume, double latitude, double longitude,
        OrderStatus status = OrderStatus.Confirmed) => new()
    {
        Id = Guid.NewGuid(), Zone = zone, Address = $"{zone} address", Latitude = latitude,
        Longitude = longitude, Volume = volume, DeliveryDate = Day, Status = status
    };

    private static Vehicle NewVehicle(decimal capacity) =>
        new() { Id = Guid.NewGuid(), RegistrationNumber = Guid.NewGuid().ToString("N"), Capacity = capacity };

    private static Driver NewDriver() =>
        new() { Id = Guid.NewGuid(), FullName = "Test driver" };
}
