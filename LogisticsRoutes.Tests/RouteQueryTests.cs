using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Services;
using LogisticsRoutes.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.Tests;

public class RouteQueryTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);

    [Fact]
    public async Task GetByIdIncludesRouteDetailsAndStopsInSequenceOrder()
    {
        var fixture = await SeedRoutesAsync();
        await using var connection = fixture.Connection;
        await using var db = fixture.Db;

        var result = await new RouteQueryService(db).GetByIdAsync(fixture.RouteId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(fixture.RouteId, result.Id);
        Assert.Equal(Day, result.Date);
        Assert.Equal(fixture.VehicleId, result.Vehicle.Id);
        Assert.Equal("ABC-123", result.Vehicle.RegistrationNumber);
        Assert.Equal(fixture.DriverId, result.Driver.Id);
        Assert.Equal("Test Driver", result.Driver.FullName);
        Assert.Equal(3.75m, result.TotalVolume);
        Assert.Equal(new[] { 1, 2 }, result.Stops.Select(stop => stop.Sequence));

        var first = result.Stops[0];
        Assert.Equal(fixture.FirstStopId, first.Id);
        Assert.Equal("Pending", first.DeliveryStatus);
        Assert.Null(first.EstimatedArrival);
        Assert.Equal(fixture.FirstOrderId, first.OrderId);
        Assert.Equal("North", first.Zone);
        Assert.Equal("First address", first.Address);
        Assert.Equal(47.01, first.Latitude);
        Assert.Equal(28.85, first.Longitude);
        Assert.Equal(1.5m, first.Volume);

        var second = result.Stops[1];
        Assert.Equal("Arrived", second.DeliveryStatus);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero),
            second.EstimatedArrival);
        Assert.Equal(2.25m, second.Volume);
    }

    [Fact]
    public async Task GetByDayReturnsOnlyRoutesFromRequestedDay()
    {
        var fixture = await SeedRoutesAsync();
        await using var connection = fixture.Connection;
        await using var db = fixture.Db;
        var service = new RouteQueryService(db);

        var requestedDay = await service.GetByDayAsync(Day, CancellationToken.None);
        var otherDay = await service.GetByDayAsync(Day.AddDays(1), CancellationToken.None);
        var emptyDay = await service.GetByDayAsync(Day.AddDays(2), CancellationToken.None);

        Assert.Equal(fixture.RouteId, Assert.Single(requestedDay).Id);
        Assert.Equal(fixture.OtherRouteId, Assert.Single(otherDay).Id);
        Assert.Empty(emptyDay);
        Assert.Equal(new[] { 1, 2 }, requestedDay[0].Stops.Select(stop => stop.Sequence));
    }

    [Fact]
    public async Task GetByIdReturnsNullWhenRouteDoesNotExist()
    {
        var fixture = await SeedRoutesAsync();
        await using var connection = fixture.Connection;
        await using var db = fixture.Db;

        var result = await new RouteQueryService(db).GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    private static async Task<RouteFixture> SeedRoutesAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var vehicle = new Vehicle { Id = Guid.NewGuid(), RegistrationNumber = "ABC-123", Capacity = 10 };
        var driver = new Driver { Id = Guid.NewGuid(), FullName = "Test Driver" };
        var firstOrder = new Order
        {
            Id = Guid.NewGuid(), Zone = "North", Address = "First address",
            Latitude = 47.01, Longitude = 28.85, Volume = 1.5m,
            DeliveryDate = Day, Status = OrderStatus.Planned
        };
        var secondOrder = new Order
        {
            Id = Guid.NewGuid(), Zone = "North", Address = "Second address",
            Latitude = 47.02, Longitude = 28.86, Volume = 2.25m,
            DeliveryDate = Day, Status = OrderStatus.Planned
        };
        var otherOrder = new Order
        {
            Id = Guid.NewGuid(), Zone = "South", Address = "Other address",
            Latitude = 46.9, Longitude = 28.7, Volume = 1,
            DeliveryDate = Day.AddDays(1), Status = OrderStatus.Planned
        };
        var firstStopId = Guid.NewGuid();
        var route = new Route
        {
            Id = Guid.NewGuid(), Date = Day, VehicleId = vehicle.Id, DriverId = driver.Id,
            Stops =
            [
                new RouteStop
                {
                    Id = Guid.NewGuid(), OrderId = secondOrder.Id, Sequence = 2,
                    DeliveryStatus = DeliveryStatus.Arrived,
                    EstimatedArrival = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero)
                },
                new RouteStop
                {
                    Id = firstStopId, OrderId = firstOrder.Id, Sequence = 1,
                    DeliveryStatus = DeliveryStatus.Pending
                }
            ]
        };
        var otherRoute = new Route
        {
            Id = Guid.NewGuid(), Date = Day.AddDays(1), VehicleId = vehicle.Id, DriverId = driver.Id,
            Stops = [new RouteStop { Id = Guid.NewGuid(), OrderId = otherOrder.Id, Sequence = 1 }]
        };
        db.AddRange(vehicle, driver, firstOrder, secondOrder, otherOrder, route, otherRoute);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new RouteFixture(connection, db, route.Id, otherRoute.Id,
            vehicle.Id, driver.Id, firstOrder.Id, firstStopId);
    }

    private record RouteFixture(
        SqliteConnection Connection,
        LogisticsDbContext Db,
        Guid RouteId,
        Guid OtherRouteId,
        Guid VehicleId,
        Guid DriverId,
        Guid FirstOrderId,
        Guid FirstStopId);
}
