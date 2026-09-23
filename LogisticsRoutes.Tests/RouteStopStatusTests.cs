using System.ComponentModel.DataAnnotations;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.BusinessLayer.Services;
using LogisticsRoutes.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.Tests;

public class RouteStopStatusTests
{
    [Theory]
    [InlineData("Delivered", OrderStatus.Delivered)]
    [InlineData("Refused", OrderStatus.Planned)]
    [InlineData("PartialReturn", OrderStatus.Planned)]
    public async Task ValidSequencePersistsFinalStopAndExpectedOrderStatus(string finalStatus,
        OrderStatus expectedOrderStatus)
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RouteStopStatusService(fixture.Db);

        foreach (var status in new[] { "Departed", "Arrived", finalStatus })
        {
            var result = await service.UpdateAsync(fixture.RouteId, fixture.StopId,
                new UpdateRouteStopStatusRequest(status), CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(status, result.DeliveryStatus);
        }

        var stop = await fixture.Db.RouteStops.AsNoTracking().SingleAsync();
        var order = await fixture.Db.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(Enum.Parse<DeliveryStatus>(finalStatus), stop.DeliveryStatus);
        Assert.Equal(expectedOrderStatus, order.Status);

        var conflict = await Assert.ThrowsAsync<RouteStopStatusConflictException>(() =>
            service.UpdateAsync(fixture.RouteId, fixture.StopId,
                new UpdateRouteStopStatusRequest("Departed"), CancellationToken.None));
        Assert.Contains("stare finală", conflict.Message);
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending, "Arrived")]
    [InlineData(DeliveryStatus.Pending, "Delivered")]
    [InlineData(DeliveryStatus.Departed, "Delivered")]
    [InlineData(DeliveryStatus.Arrived, "Pending")]
    [InlineData(DeliveryStatus.Arrived, "Arrived")]
    public async Task InvalidTransitionReturnsConflictWithoutSaving(DeliveryStatus current, string requested)
    {
        await using var fixture = await Fixture.CreateAsync(current);
        var service = new RouteStopStatusService(fixture.Db);

        var conflict = await Assert.ThrowsAsync<RouteStopStatusConflictException>(() =>
            service.UpdateAsync(fixture.RouteId, fixture.StopId,
                new UpdateRouteStopStatusRequest(requested), CancellationToken.None));

        Assert.Contains("Tranziție nepermisă", conflict.Message);
        Assert.Equal(current, (await fixture.Db.RouteStops.AsNoTracking().SingleAsync()).DeliveryStatus);
        Assert.Equal(OrderStatus.Planned, (await fixture.Db.Orders.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task StopMustBelongToSpecifiedRoute()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RouteStopStatusService(fixture.Db);

        var result = await service.UpdateAsync(Guid.NewGuid(), fixture.StopId,
            new UpdateRouteStopStatusRequest("Departed"), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(DeliveryStatus.Pending,
            (await fixture.Db.RouteStops.AsNoTracking().SingleAsync()).DeliveryStatus);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("1")]
    [InlineData(null)]
    public async Task UnknownStatusIsRejected(string? status)
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RouteStopStatusService(fixture.Db);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(fixture.RouteId, fixture.StopId,
                new UpdateRouteStopStatusRequest(status), CancellationToken.None));

        Assert.Contains("Status necunoscut", error.Message);
        Assert.Equal(DeliveryStatus.Pending,
            (await fixture.Db.RouteStops.AsNoTracking().SingleAsync()).DeliveryStatus);
    }

    private sealed class Fixture(SqliteConnection connection, LogisticsDbContext db, Guid routeId, Guid stopId)
        : IAsyncDisposable
    {
        public LogisticsDbContext Db { get; } = db;
        public Guid RouteId { get; } = routeId;
        public Guid StopId { get; } = stopId;

        public static async Task<Fixture> CreateAsync(DeliveryStatus initialStatus = DeliveryStatus.Pending)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();

            var order = new Order
            {
                Id = Guid.NewGuid(), Zone = "Centru", Address = "Strada Test 1",
                Latitude = 47, Longitude = 28.8, Volume = 2,
                DeliveryDate = new DateOnly(2026, 9, 24), Status = OrderStatus.Planned
            };
            var route = new Route
            {
                Id = Guid.NewGuid(), Date = order.DeliveryDate,
                Vehicle = new Vehicle { Id = Guid.NewGuid(), RegistrationNumber = "ABC-123", Capacity = 10 },
                Driver = new Driver { Id = Guid.NewGuid(), FullName = "Șofer Test" },
                Stops = [new RouteStop
                {
                    Id = Guid.NewGuid(), Order = order, Sequence = 1, DeliveryStatus = initialStatus
                }]
            };
            db.Routes.Add(route);
            await db.SaveChangesAsync();
            return new Fixture(connection, db, route.Id, route.Stops[0].Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
