using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.BusinessLayer.Services;
using LogisticsRoutes.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.Tests;

public class RemainingRoutePlanningTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static readonly PlanningSettings Depot = new(47, 28.8);

    [Fact]
    public async Task ConfirmedOversizedOrderCanBeCorrectedAndPlannedWithoutChangingExistingRoute()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var live = NewOrder("live-test", 2, OrderStatus.Planned);
        var blocked = NewOrder("Centru", 13, OrderStatus.Confirmed);
        var existing = NewRoute(live, 12.5m);
        var freeVehicle = NewVehicle(12.5m);
        var freeDriver = NewDriver();
        db.Routes.Add(existing);
        db.Orders.Add(blocked);
        db.Vehicles.Add(freeVehicle);
        db.Drivers.Add(freeDriver);
        await db.SaveChangesAsync();
        var originalStopId = existing.Stops[0].Id;

        var corrected = await new OrderService(db).CorrectConfirmedVolumeAsync(blocked.Id,
            new CorrectConfirmedVolumeRequest(5, 13), CancellationToken.None);
        Assert.Equal(5, corrected!.Volume);
        Assert.Equal(nameof(OrderStatus.Confirmed), corrected.Status);

        db.ChangeTracker.Clear();
        var planned = await new RemainingRoutePlanningService(db, Depot)
            .PlanAsync(new PlanRemainingRequest(Day, [blocked.Id]), CancellationToken.None);
        var newRoute = Assert.Single(planned.CreatedRoutes);
        Assert.Equal("Centru", newRoute.Zone);
        Assert.Equal(blocked.Id, Assert.Single(newRoute.Stops).OrderId);
        Assert.Equal(freeVehicle.Id, newRoute.VehicleId);
        Assert.Equal(freeDriver.Id, newRoute.DriverId);
        db.ChangeTracker.Clear();
        Assert.Equal(originalStopId, (await db.RouteStops.SingleAsync(stop => stop.RouteId == existing.Id)).Id);
        Assert.Equal(OrderStatus.Planned, (await db.Orders.SingleAsync(order => order.Id == blocked.Id)).Status);
    }

    [Fact]
    public async Task ConfirmedCorrectionRejectsInvalidVolumeFleetChangeAndStaleVolume()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var order = NewOrder("Centru", 13, OrderStatus.Confirmed);
        var vehicle = NewVehicle(12.5m);
        db.Orders.Add(order);
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync();
        var service = new OrderService(db);

        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            service.CorrectConfirmedVolumeAsync(order.Id, new CorrectConfirmedVolumeRequest(0, 13), CancellationToken.None));
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            service.CorrectConfirmedVolumeAsync(order.Id, new CorrectConfirmedVolumeRequest(1.1234m, 13), CancellationToken.None));
        var oversized = await Assert.ThrowsAsync<OrderConflictException>(() =>
            service.CorrectConfirmedVolumeAsync(order.Id, new CorrectConfirmedVolumeRequest(13, 13), CancellationToken.None));
        Assert.Contains("12.5", oversized.Message);

        await db.Vehicles.Where(item => item.Id == vehicle.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Capacity, 4));
        await Assert.ThrowsAsync<OrderConflictException>(() =>
            service.CorrectConfirmedVolumeAsync(order.Id, new CorrectConfirmedVolumeRequest(5, 13), CancellationToken.None));
        await db.Orders.Where(item => item.Id == order.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Volume, 3));
        await Assert.ThrowsAsync<OrderConflictException>(() =>
            service.CorrectConfirmedVolumeAsync(order.Id, new CorrectConfirmedVolumeRequest(2, 13), CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(3, (await db.Orders.SingleAsync(item => item.Id == order.Id)).Volume);
    }

    [Fact]
    public async Task CorrectionReadBeforePlanningCannotChangeAssignedOrder()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var order = NewOrder("Centru", 5, OrderStatus.Confirmed);
        db.Orders.Add(order);
        db.Vehicles.Add(NewVehicle(12.5m));
        db.Drivers.Add(NewDriver());
        await db.SaveChangesAsync();
        var volumeSeenByEditor = order.Volume;

        await new RemainingRoutePlanningService(db, Depot)
            .PlanAsync(new PlanRemainingRequest(Day, [order.Id]), CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<OrderConflictException>(() =>
            new OrderService(db).CorrectConfirmedVolumeAsync(order.Id,
                new CorrectConfirmedVolumeRequest(4, volumeSeenByEditor), CancellationToken.None));
        Assert.Contains("rute", conflict.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(5, (await db.Orders.SingleAsync(item => item.Id == order.Id)).Volume);
        Assert.Equal(1, await db.RouteStops.CountAsync(stop => stop.OrderId == order.Id));
    }

    [Fact]
    public async Task ConfirmedCorrectionAlsoRejectsAnExistingStopAndNewStatus()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var assigned = NewOrder("Centru", 5, OrderStatus.Confirmed);
        var draft = NewOrder("Centru", 5, OrderStatus.New);
        db.Routes.Add(NewRoute(assigned, 12.5m));
        db.Orders.Add(draft);
        await db.SaveChangesAsync();
        var service = new OrderService(db);

        await Assert.ThrowsAsync<OrderConflictException>(() =>
            service.CorrectConfirmedVolumeAsync(assigned.Id,
                new CorrectConfirmedVolumeRequest(4, 5), CancellationToken.None));
        await Assert.ThrowsAsync<OrderConflictException>(() =>
            service.CorrectConfirmedVolumeAsync(draft.Id,
                new CorrectConfirmedVolumeRequest(4, 5), CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(5, (await db.Orders.SingleAsync(order => order.Id == assigned.Id)).Volume);
        Assert.Equal(5, (await db.Orders.SingleAsync(order => order.Id == draft.Id)).Volume);
    }

    [Fact]
    public async Task OversizedDraftIsRejectedThenCorrectedAndPlannedIntoNewZoneRoute()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var live = NewOrder("live-test", 2, OrderStatus.Planned);
        var newOrder = NewOrder("Centru", 13, OrderStatus.New);
        var existing = NewRoute(live, 12.5m);
        var freeVehicle = NewVehicle(12.5m);
        var freeDriver = NewDriver();
        db.Routes.Add(existing);
        db.Orders.Add(newOrder);
        db.Vehicles.Add(freeVehicle);
        db.Drivers.Add(freeDriver);
        await db.SaveChangesAsync();
        var originalStop = Assert.Single(existing.Stops);

        var orders = new OrderService(db);
        var conflict = await Assert.ThrowsAsync<OrderConflictException>(() =>
            orders.ConfirmAsync(newOrder.Id, CancellationToken.None));
        Assert.Contains("12.5", conflict.Message);
        Assert.Equal(OrderStatus.New, newOrder.Status);
        await orders.UpdateVolumeAsync(newOrder.Id, new UpdateOrderVolumeRequest(5), CancellationToken.None);
        await orders.ConfirmAsync(newOrder.Id, CancellationToken.None);
        db.ChangeTracker.Clear();

        var planned = await new RemainingRoutePlanningService(db, Depot)
            .PlanAsync(new PlanRemainingRequest(Day), CancellationToken.None);
        var created = Assert.Single(planned.CreatedRoutes);
        Assert.Empty(planned.ExtendedRoutes);
        Assert.Equal("Centru", created.Zone);
        Assert.Equal(freeVehicle.Id, created.VehicleId);
        Assert.Equal(freeDriver.Id, created.DriverId);
        Assert.Equal(newOrder.Id, Assert.Single(created.Stops).OrderId);

        db.ChangeTracker.Clear();
        var unchanged = await db.Routes.Include(route => route.Stops).SingleAsync(route => route.Id == existing.Id);
        Assert.Equal(existing.VehicleId, unchanged.VehicleId);
        Assert.Equal(existing.DriverId, unchanged.DriverId);
        Assert.Equal(originalStop.Id, Assert.Single(unchanged.Stops).Id);
        Assert.Equal(1, unchanged.Stops[0].Sequence);
        Assert.Equal(OrderStatus.Planned, (await db.Orders.SingleAsync(order => order.Id == newOrder.Id)).Status);
    }

    [Fact]
    public async Task CompatibleExistingRouteKeepsItsStopAndUsesNoNewResource()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var existingOrder = NewOrder("Centru", 2, OrderStatus.Planned);
        var added = NewOrder("Centru", 1, OrderStatus.Confirmed);
        var route = NewRoute(existingOrder, 5);
        db.Routes.Add(route);
        db.Orders.Add(added);
        await db.SaveChangesAsync();
        var oldStopId = route.Stops[0].Id;

        var result = await new RemainingRoutePlanningService(db, Depot)
            .PlanAsync(new PlanRemainingRequest(Day, [added.Id]), CancellationToken.None);
        Assert.Empty(result.CreatedRoutes);
        Assert.Equal(added.Id, Assert.Single(Assert.Single(result.ExtendedRoutes).AddedStops).OrderId);

        db.ChangeTracker.Clear();
        var stops = await db.RouteStops.Where(stop => stop.RouteId == route.Id)
            .OrderBy(stop => stop.Sequence).ToListAsync();
        Assert.Equal(2, stops.Count);
        Assert.Equal(oldStopId, stops[0].Id);
        Assert.Equal(1, stops[0].Sequence);
        Assert.Equal(added.Id, stops[1].OrderId);
    }

    [Fact]
    public async Task InsufficientResourcesRollsBackAllAssignments()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var existingOrder = NewOrder("Centru", 2, OrderStatus.Planned);
        var fitsExisting = NewOrder("Centru", 1, OrderStatus.Confirmed);
        var needsNewRoute = NewOrder("Sud", 1, OrderStatus.Confirmed);
        var route = NewRoute(existingOrder, 5);
        db.Routes.Add(route);
        db.Orders.AddRange(fitsExisting, needsNewRoute);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<PlanningConflictException>(() =>
            new RemainingRoutePlanningService(db, Depot)
                .PlanAsync(new PlanRemainingRequest(Day), CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Routes.CountAsync());
        Assert.Equal(1, await db.RouteStops.CountAsync());
        Assert.Equal(2, await db.Orders.CountAsync(order => order.Status == OrderStatus.Confirmed));
    }

    [Fact]
    public async Task ConfirmReadsCurrentFleetAfterVolumeEdit()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var draft = NewOrder("Centru", 2, OrderStatus.New);
        var vehicle = NewVehicle(12.5m);
        db.Orders.Add(draft);
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync();
        var orders = new OrderService(db);
        await orders.UpdateVolumeAsync(draft.Id, new UpdateOrderVolumeRequest(12), CancellationToken.None);
        await db.Vehicles.Where(item => item.Id == vehicle.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Capacity, 10));

        await Assert.ThrowsAsync<OrderConflictException>(() =>
            orders.ConfirmAsync(draft.Id, CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(OrderStatus.New, (await db.Orders.SingleAsync(order => order.Id == draft.Id)).Status);
    }

    [Fact]
    public async Task DatabasePreventsReusingVehicleOrDriverOnSameDay()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var existing = NewRoute(NewOrder("Centru", 1, OrderStatus.Planned), 5);
        db.Routes.Add(existing);
        await db.SaveChangesAsync();

        db.Routes.Add(new Route
        {
            Id = Guid.NewGuid(), Date = Day, VehicleId = existing.VehicleId, Driver = NewDriver()
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Routes.CountAsync());

        db.Routes.Add(new Route
        {
            Id = Guid.NewGuid(), Date = Day, Vehicle = NewVehicle(5), DriverId = existing.DriverId
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Routes.CountAsync());
    }

    private static Order NewOrder(string zone, decimal volume, OrderStatus status) => new()
    {
        Id = Guid.NewGuid(), Zone = zone, Address = $"{zone} test", Latitude = 47,
        Longitude = 28.8, Volume = volume, DeliveryDate = Day, Status = status
    };

    private static Vehicle NewVehicle(decimal capacity) => new()
    {
        Id = Guid.NewGuid(), RegistrationNumber = Guid.NewGuid().ToString("N"), Capacity = capacity
    };

    private static Driver NewDriver() => new() { Id = Guid.NewGuid(), FullName = "Șofer test" };

    private static Route NewRoute(Order order, decimal capacity)
    {
        var route = new Route
        {
            Id = Guid.NewGuid(), Date = Day, Vehicle = NewVehicle(capacity), Driver = NewDriver()
        };
        route.Stops.Add(new RouteStop { Id = Guid.NewGuid(), Order = order, Sequence = 1 });
        return route;
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
}
