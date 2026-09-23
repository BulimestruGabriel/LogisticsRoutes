using System.Net;
using System.Net.Http.Json;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
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

public class RouteStopStatusEndpointTests
{
    [Fact]
    public async Task PatchChecksStatusRouteOwnershipAndPersistsDeliveredOrder()
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

        var order = new Order
        {
            Id = Guid.NewGuid(), Zone = "Centru", Address = "Strada Test 1",
            Latitude = 47, Longitude = 28.8, Volume = 2,
            DeliveryDate = new DateOnly(2026, 9, 24), Status = OrderStatus.Planned
        };
        var stop = new RouteStop { Id = Guid.NewGuid(), Order = order, Sequence = 1 };
        var route = NewRoute(order.DeliveryDate);
        route.Stops.Add(stop);
        var otherRoute = NewRoute(order.DeliveryDate);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Routes.AddRange(route, otherRoute);
            await db.SaveChangesAsync();
        }

        var url = $"/api/routes/{route.Id}/stops/{stop.Id}/status";
        var badStatus = await client.PatchAsJsonAsync(url, new UpdateRouteStopStatusRequest("Unknown"));
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Contains("Status necunoscut", (await badStatus.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);

        foreach (var missingUrl in new[]
                 {
                     $"/api/routes/{otherRoute.Id}/stops/{stop.Id}/status",
                     $"/api/routes/{route.Id}/stops/{Guid.NewGuid()}/status",
                     $"/api/routes/{Guid.NewGuid()}/stops/{stop.Id}/status"
                 })
        {
            var missing = await client.PatchAsJsonAsync(missingUrl,
                new UpdateRouteStopStatusRequest("Departed"));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        var invalid = await client.PatchAsJsonAsync(url, new UpdateRouteStopStatusRequest("Delivered"));
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        Assert.Contains("Tranziție nepermisă", (await invalid.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);

        foreach (var status in new[] { "Departed", "Arrived", "Delivered" })
        {
            var response = await client.PatchAsJsonAsync(url, new UpdateRouteStopStatusRequest(status));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var updated = await response.Content.ReadFromJsonAsync<UpdateRouteStopStatusResponse>();
            Assert.NotNull(updated);
            Assert.Equal(stop.Id, updated.StopId);
            Assert.Equal(status, updated.DeliveryStatus);
            Assert.Equal(status == "Delivered" ? "Delivered" : "Planned", updated.OrderStatus);
        }

        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        Assert.Equal(DeliveryStatus.Delivered,
            (await checkDb.RouteStops.AsNoTracking().SingleAsync()).DeliveryStatus);
        Assert.Equal(OrderStatus.Delivered,
            (await checkDb.Orders.AsNoTracking().SingleAsync()).Status);
    }

    private static Route NewRoute(DateOnly day) => new()
    {
        Id = Guid.NewGuid(), Date = day,
        Vehicle = new Vehicle { Id = Guid.NewGuid(), RegistrationNumber = Guid.NewGuid().ToString("N"), Capacity = 10 },
        Driver = new Driver { Id = Guid.NewGuid(), FullName = "Șofer Test" }
    };
}
