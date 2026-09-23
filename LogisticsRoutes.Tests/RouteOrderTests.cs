using System.Net;
using System.Net.Http.Json;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.BusinessLayer.Services;
using LogisticsRoutes.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LogisticsRoutes.Tests;

public class RouteOrderTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);
    private static readonly PlanningSettings Depot = new(0, 0);

    [Fact]
    public async Task AddingOrderRecalculatesEverySequenceAndPreservesExistingStops()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var (route, first, second, added) = await SeedAsync(db);

        var response = await new RouteOrderService(db, Depot)
            .AddAsync(route.Id, new AddRouteOrderRequest(added.Id), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(new[] { added.Id, second.OrderId, first.OrderId },
            response.Stops.Select(stop => stop.OrderId));
        Assert.Equal(new[] { 1, 2, 3 }, response.Stops.Select(stop => stop.Sequence));
        Assert.Equal(5, response.TotalVolume);
        Assert.Equal(10, response.Vehicle.Capacity);
        Assert.Equal(first.Id, response.Stops.Single(stop => stop.OrderId == first.OrderId).Id);
        Assert.Equal(second.Id, response.Stops.Single(stop => stop.OrderId == second.OrderId).Id);
        Assert.Equal(first.EstimatedArrival,
            response.Stops.Single(stop => stop.OrderId == first.OrderId).EstimatedArrival);
        Assert.All(response.Stops, stop => Assert.Equal(nameof(DeliveryStatus.Pending), stop.DeliveryStatus));

        db.ChangeTracker.Clear();
        Assert.Equal(OrderStatus.Planned, (await db.Orders.SingleAsync(order => order.Id == added.Id)).Status);
        Assert.Equal(new[] { added.Id, second.OrderId, first.OrderId },
            (await db.RouteStops.OrderBy(stop => stop.Sequence).ToListAsync()).Select(stop => stop.OrderId));
    }

    [Fact]
    public async Task CapacityConflictLeavesDatabaseUnchanged()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var (route, _, _, added) = await SeedAsync(db);
        added.Volume = 7;
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<RouteOrderConflictException>(() =>
            new RouteOrderService(db, Depot).AddAsync(route.Id, new AddRouteOrderRequest(added.Id),
                CancellationToken.None));

        Assert.Contains("Capacitatea", error.Message);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DifferentDayOrZoneLeavesDatabaseUnchanged(bool differentDay)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var (route, _, _, added) = await SeedAsync(db);
        if (differentDay) added.DeliveryDate = Day.AddDays(1);
        else added.Zone = "Sud";
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<RouteOrderConflictException>(() =>
            new RouteOrderService(db, Depot).AddAsync(route.Id, new AddRouteOrderRequest(added.Id),
                CancellationToken.None));

        Assert.Contains(differentDay ? "zi" : "zonă", error.Message);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlannedOrderWithOrWithoutAnotherRouteLeavesDatabaseUnchanged(bool assigned)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var (route, _, _, added) = await SeedAsync(db);
        added.Status = OrderStatus.Planned;
        if (assigned)
        {
            var other = NewRoute();
            other.Stops.Add(new RouteStop { Id = Guid.NewGuid(), OrderId = added.Id, Sequence = 1 });
            db.Routes.Add(other);
        }
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<RouteOrderConflictException>(() =>
            new RouteOrderService(db, Depot).AddAsync(route.Id, new AddRouteOrderRequest(added.Id),
                CancellationToken.None));

        Assert.Contains(assigned ? "aparține deja" : "confirmată", error.Message);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task StartedStopLeavesDatabaseUnchanged()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var (route, first, _, added) = await SeedAsync(db);
        first.DeliveryStatus = DeliveryStatus.Departed;
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<RouteOrderConflictException>(() =>
            new RouteOrderService(db, Depot).AddAsync(route.Id, new AddRouteOrderRequest(added.Id),
                CancellationToken.None));

        Assert.Contains("oprire începută", error.Message);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackTemporarySequencesAndOrderStatus()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var (route, _, _, added) = await SeedAsync(db);
        var before = await SnapshotAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_order_status_update
            BEFORE UPDATE OF Status ON Orders
            BEGIN SELECT RAISE(ABORT, 'simulated database failure'); END;
            """);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            new RouteOrderService(db, Depot).AddAsync(route.Id, new AddRouteOrderRequest(added.Id),
                CancellationToken.None));

        Assert.Contains("simulated database failure", error.ToString());
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task EndpointReturnsRouteAndClearErrorsWithoutChanges()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:LogisticsDb"] = "Host=localhost;Database=unused",
                    ["Planning:DepotLatitude"] = "0",
                    ["Planning:DepotLongitude"] = "0"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<LogisticsDbContext>();
                services.RemoveAll<DbContextOptions<LogisticsDbContext>>();
                services.AddDbContext<LogisticsDbContext>(options => options.UseSqlite(connection));
            });
        });
        using var client = factory.CreateClient();
        Route route;
        Order added;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            (route, _, _, added) = await SeedAsync(db);
        }

        var url = $"/api/routes/{route.Id}/orders";
        var before = await SnapshotFromFactoryAsync(factory);
        var invalid = await client.PostAsJsonAsync(url, new AddRouteOrderRequest(Guid.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(before, await SnapshotFromFactoryAsync(factory));

        foreach (var missingUrl in new[] { $"/api/routes/{Guid.NewGuid()}/orders", url })
        {
            var missing = await client.PostAsJsonAsync(missingUrl,
                new AddRouteOrderRequest(missingUrl == url ? Guid.NewGuid() : added.Id));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Contains("nu a fost găsită", (await missing.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
            Assert.Equal(before, await SnapshotFromFactoryAsync(factory));
        }

        var success = await client.PostAsJsonAsync(url, new AddRouteOrderRequest(added.Id));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var updated = await success.Content.ReadFromJsonAsync<RouteResponse>();
        Assert.NotNull(updated);
        Assert.Equal(route.Id, updated.Id);
        Assert.Equal(3, updated.Stops.Count);

        var after = await SnapshotFromFactoryAsync(factory);
        var duplicate = await client.PostAsJsonAsync(url, new AddRouteOrderRequest(added.Id));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("aparține deja", (await duplicate.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
        Assert.Equal(after, await SnapshotFromFactoryAsync(factory));
    }

    private static async Task<(Route route, RouteStop first, RouteStop second, Order added)> SeedAsync(
        LogisticsDbContext db)
    {
        var firstOrder = NewOrder("Nord", 2, 1, OrderStatus.Planned);
        var secondOrder = NewOrder("Nord", 2, -1.1, OrderStatus.Planned);
        var added = NewOrder("Nord", 1, -0.5, OrderStatus.Confirmed);
        var route = NewRoute();
        var first = new RouteStop
        {
            Id = Guid.NewGuid(), Order = firstOrder, Sequence = 1,
            EstimatedArrival = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
        };
        var second = new RouteStop { Id = Guid.NewGuid(), Order = secondOrder, Sequence = 2 };
        route.Stops.AddRange([first, second]);
        db.Orders.Add(added);
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        return (route, first, second, added);
    }

    private static Route NewRoute() => new()
    {
        Id = Guid.NewGuid(), Date = Day,
        Vehicle = new Vehicle { Id = Guid.NewGuid(), RegistrationNumber = Guid.NewGuid().ToString("N"), Capacity = 10 },
        Driver = new Driver { Id = Guid.NewGuid(), FullName = "Șofer test" }
    };

    private static Order NewOrder(string zone, decimal volume, double longitude, OrderStatus status) => new()
    {
        Id = Guid.NewGuid(), Zone = zone, Address = $"Adresa {longitude}",
        Latitude = 0, Longitude = longitude, Volume = volume, DeliveryDate = Day, Status = status
    };

    private static async Task<string> SnapshotFromFactoryAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return await SnapshotAsync(scope.ServiceProvider.GetRequiredService<LogisticsDbContext>());
    }

    private static async Task<string> SnapshotAsync(LogisticsDbContext db)
    {
        var stops = await db.RouteStops.AsNoTracking().OrderBy(stop => stop.Id)
            .Select(stop => $"{stop.Id}:{stop.RouteId}:{stop.OrderId}:{stop.Sequence}:{stop.DeliveryStatus}")
            .ToListAsync();
        var orders = await db.Orders.AsNoTracking().OrderBy(order => order.Id)
            .Select(order => $"{order.Id}:{order.Status}:{order.Zone}:{order.DeliveryDate}:{order.Volume}")
            .ToListAsync();
        return string.Join('|', stops.Concat(orders));
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
