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

public class RoutePositionEndpointTests
{
    [Fact]
    public async Task ReportValidatesCoordinatesAndRouteAndPersistsOnlyLatestPosition()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
        using var client = factory.CreateClient();

        var route = new Route
        {
            Id = Guid.NewGuid(), Date = new DateOnly(2026, 9, 25),
            Vehicle = new Vehicle { Id = Guid.NewGuid(), RegistrationNumber = "TEST-POS", Capacity = 10 },
            Driver = new Driver { Id = Guid.NewGuid(), FullName = "Șofer Test" }
        };
        var order = new Order
        {
            Id = Guid.NewGuid(), Zone = "Centru", Address = "Test 1",
            Latitude = 47.01, Longitude = 28.85, Volume = 2,
            DeliveryDate = route.Date, Status = OrderStatus.Planned
        };
        var stop = new RouteStop { Id = Guid.NewGuid(), Order = order, Sequence = 1 };
        route.Stops.Add(stop);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Routes.Add(route);
            await db.SaveChangesAsync();
        }

        var url = $"/api/routes/{route.Id}/position";
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(url)).StatusCode);

        var moment = DateTimeOffset.UtcNow.AddSeconds(-5);
        foreach (var bad in new[]
                 {
                     new ReportRoutePositionRequest(90.01, 28.85, moment, "Reported"),
                     new ReportRoutePositionRequest(47.01, -180.01, moment, "Reported"),
                     new ReportRoutePositionRequest(null, 28.85, moment, "Reported"),
                     new ReportRoutePositionRequest(47.01, null, moment, "Reported"),
                     new ReportRoutePositionRequest(47.01, 28.85, default, "Reported"),
                     new ReportRoutePositionRequest(47.01, 28.85, DateTimeOffset.UtcNow.AddMinutes(6), "Reported"),
                     new ReportRoutePositionRequest(47.01, 28.85, moment, "Unknown")
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, bad)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(url)).StatusCode);

        var missingUrl = $"/api/routes/{Guid.NewGuid()}/position";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(missingUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(missingUrl,
                new ReportRoutePositionRequest(47.01, 28.85, moment, "Simulated"))).StatusCode);

        var first = await client.PostAsJsonAsync(url,
            new ReportRoutePositionRequest(47.01, 28.85, moment, "Simulated"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPosition = await first.Content.ReadFromJsonAsync<RoutePositionResponse>();
        Assert.NotNull(firstPosition);
        Assert.Equal("Simulated", firstPosition.Source);
        Assert.Equal(route.Id, firstPosition.RouteId);
        Assert.True(firstPosition.ReceivedAt >= moment);

        var newerMoment = moment.AddSeconds(1);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url,
            new ReportRoutePositionRequest(47.02, 28.86, newerMoment, "Simulated"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url,
            new ReportRoutePositionRequest(47, 28, moment, "Simulated"))).StatusCode);

        var read = await client.GetFromJsonAsync<RoutePositionResponse>(url);
        Assert.NotNull(read);
        Assert.Equal(47.02, read.Latitude);
        Assert.Equal(28.86, read.Longitude);
        Assert.Equal(newerMoment, read.ReportedAt);

        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        Assert.Equal(1, await checkDb.RoutePositions.CountAsync());
        var savedStop = await checkDb.RouteStops.AsNoTracking().SingleAsync();
        Assert.Equal(stop.Id, savedStop.Id);
        Assert.Equal(1, savedStop.Sequence);
        Assert.Equal(DeliveryStatus.Pending, savedStop.DeliveryStatus);
        Assert.Equal(OrderStatus.Planned, (await checkDb.Orders.AsNoTracking().SingleAsync()).Status);
    }
}
