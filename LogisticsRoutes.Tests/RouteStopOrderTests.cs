using System.Net;
using System.Net.Http.Json;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LogisticsRoutes.Tests;

public class RouteStopOrderTests
{
    [Fact]
    public async Task ReorderingChangesPositionsAndPersistsAfterReadingAgain()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (routeId, stops) = await SeedAsync(factory);
        var proposed = new[] { stops[2].Id, stops[0].Id, stops[1].Id };

        var response = await client.PutAsJsonAsync($"/api/routes/{routeId}/stops/order",
            new ReorderRouteStopsRequest(proposed.ToList()));

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var updated = await response.Content.ReadFromJsonAsync<RouteResponse>();
        Assert.NotNull(updated);
        Assert.Equal(proposed, updated.Stops.Select(stop => stop.Id));
        Assert.Equal(new[] { 1, 2, 3 }, updated.Stops.Select(stop => stop.Sequence));
        foreach (var original in stops)
        {
            var current = updated.Stops.Single(stop => stop.Id == original.Id);
            Assert.Equal(original.OrderId, current.OrderId);
            Assert.Equal(nameof(DeliveryStatus.Pending), current.DeliveryStatus);
            Assert.Equal(original.EstimatedArrival, current.EstimatedArrival);
        }

        var reread = await client.GetFromJsonAsync<RouteResponse>($"/api/routes/{routeId}");
        Assert.NotNull(reread);
        Assert.Equal(proposed, reread.Stops.Select(stop => stop.Id));
        Assert.Equal(new[] { 1, 2, 3 }, reread.Stops.Select(stop => stop.Sequence));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        Assert.Equal(proposed, await db.RouteStops.OrderBy(stop => stop.Sequence)
            .Select(stop => stop.Id).ToArrayAsync());
        Assert.All(await db.Orders.ToListAsync(), order => Assert.Equal(OrderStatus.Planned, order.Status));
    }

    [Fact]
    public async Task DuplicateMissingAndForeignIdsReturnBadRequestWithoutChanges()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (routeId, stops) = await SeedAsync(factory);
        var before = await ReadStopIdsAsync(client, routeId);

        foreach (var invalid in new[]
                 {
                     new[] { stops[0].Id, stops[0].Id, stops[2].Id },
                     new[] { stops[0].Id, stops[1].Id },
                     new[] { stops[0].Id, stops[1].Id, Guid.NewGuid() }
                 })
        {
            var response = await client.PutAsJsonAsync($"/api/routes/{routeId}/stops/order",
                new ReorderRouteStopsRequest(invalid.ToList()));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, await ReadStopIdsAsync(client, routeId));
        }
    }

    [Fact]
    public async Task StartedRouteReturnsConflictWithoutChangingSequences()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (routeId, stops) = await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            var stop = await db.RouteStops.SingleAsync(item => item.Id == stops[1].Id);
            stop.DeliveryStatus = DeliveryStatus.Departed;
            await db.SaveChangesAsync();
        }

        var response = await client.PutAsJsonAsync($"/api/routes/{routeId}/stops/order",
            new ReorderRouteStopsRequest(stops.Select(stop => stop.Id).Reverse().ToList()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(stops.Select(stop => stop.Id), await ReadStopIdsAsync(client, routeId));
    }

    [Fact]
    public async Task MissingRouteReturnsNotFound()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/routes/{Guid.NewGuid()}/stops/order",
            new ReorderRouteStopsRequest([]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FailureDuringFinalPositionsRollsBackTemporarySequences()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (routeId, stops) = await SeedAsync(factory);
        await using (var db = new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>()
                         .UseSqlite(connection).Options))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_second_position
                BEFORE UPDATE OF Sequence ON RouteStops
                WHEN NEW.Sequence = 2 AND OLD.Sequence < 0
                BEGIN SELECT RAISE(ABORT, 'simulated reorder failure'); END;
                """);
        }

        var response = await client.PutAsJsonAsync($"/api/routes/{routeId}/stops/order",
            new ReorderRouteStopsRequest(stops.Select(stop => stop.Id).Reverse().ToList()));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(stops.Select(stop => stop.Id), await ReadStopIdsAsync(client, routeId));
    }

    private static async Task<Guid[]> ReadStopIdsAsync(HttpClient client, Guid routeId)
    {
        var route = await client.GetFromJsonAsync<RouteResponse>($"/api/routes/{routeId}");
        return route!.Stops.Select(stop => stop.Id).ToArray();
    }

    private static async Task<(Guid routeId, RouteStop[] stops)> SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        var route = new Route
        {
            Id = Guid.NewGuid(), Date = new DateOnly(2026, 10, 1),
            Vehicle = new Vehicle { Id = Guid.NewGuid(), RegistrationNumber = "TEST-ORDER", Capacity = 10 },
            Driver = new Driver { Id = Guid.NewGuid(), FullName = "Șofer test" }
        };
        var stops = Enumerable.Range(1, 3).Select(index => new RouteStop
        {
            Id = Guid.NewGuid(), Sequence = index,
            EstimatedArrival = new DateTimeOffset(2026, 10, 1, 12 + index, 0, 0, TimeSpan.Zero),
            Order = new Order
            {
                Id = Guid.NewGuid(), DeliveryDate = route.Date, Zone = "Nord",
                Address = $"Adresa {index}", Latitude = 47, Longitude = 28 + index,
                Volume = 1, Status = OrderStatus.Planned
            }
        }).ToArray();
        route.Stops.AddRange(stops);
        db.Routes.Add(route);
        await db.SaveChangesAsync();
        return (route.Id, stops);
    }

    private static WebApplicationFactory<Program> CreateFactory(SqliteConnection connection) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:LogisticsDb"] = "Host=localhost;Database=unused"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<LogisticsDbContext>();
                services.RemoveAll<DbContextOptions<LogisticsDbContext>>();
                services.AddDbContext<LogisticsDbContext>(options => options.UseSqlite(connection));
            });
        });

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return connection;
    }
}
