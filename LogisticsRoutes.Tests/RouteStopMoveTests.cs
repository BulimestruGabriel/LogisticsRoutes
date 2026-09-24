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

public class RouteStopMoveTests
{
    [Fact]
    public async Task MoveKeepsStopAndOrderAndRenumbersBothRoutes()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (source, destination) = await SeedAsync(factory);
        var moved = source.Stops[1];

        var response = await MoveAsync(client, source.Id, moved.Id, destination.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var rereadSource = await client.GetFromJsonAsync<RouteResponse>($"/api/routes/{source.Id}");
        var rereadDestination = await client.GetFromJsonAsync<RouteResponse>($"/api/routes/{destination.Id}");
        Assert.NotNull(rereadSource);
        Assert.NotNull(rereadDestination);
        Assert.Equal(new[] { source.Stops[0].Id, source.Stops[2].Id },
            rereadSource.Stops.Select(stop => stop.Id));
        Assert.Equal(new[] { destination.Stops[0].Id, destination.Stops[1].Id, moved.Id },
            rereadDestination.Stops.Select(stop => stop.Id));
        Assert.Equal(new[] { 1, 2 }, rereadSource.Stops.Select(stop => stop.Sequence));
        Assert.Equal(new[] { 1, 2, 3 }, rereadDestination.Stops.Select(stop => stop.Sequence));
        Assert.Equal(4, rereadSource.TotalVolume);
        Assert.Equal(5, rereadDestination.TotalVolume);
        var movedAgain = rereadDestination.Stops[2];
        Assert.Equal(moved.OrderId, movedAgain.OrderId);
        Assert.Equal(moved.EstimatedArrival, movedAgain.EstimatedArrival);
        Assert.Equal(nameof(DeliveryStatus.Pending), movedAgain.DeliveryStatus);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        Assert.Equal(destination.Id, (await db.RouteStops.AsNoTracking()
            .SingleAsync(stop => stop.Id == moved.Id)).RouteId);
        Assert.All(await db.Orders.AsNoTracking().ToListAsync(),
            order => Assert.Equal(OrderStatus.Planned, order.Status));
    }

    [Fact]
    public async Task CapacityExceededReturnsConflictWithoutChanges()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (source, destination) = await SeedAsync(factory, destinationCapacity: 4);
        var before = await SnapshotAsync(factory);

        var response = await MoveAsync(client, source.Id, source.Stops[1].Id, destination.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Capacitatea", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DifferentDayOrZoneReturnsConflictWithoutChanges(bool differentDay)
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (source, destination) = await SeedAsync(factory,
            destinationDay: differentDay ? new DateOnly(2026, 10, 2) : null,
            destinationZone: differentDay ? "Nord" : "Sud");
        var before = await SnapshotAsync(factory);

        var response = await MoveAsync(client, source.Id, source.Stops[1].Id, destination.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(differentDay ? "zi" : "zonă", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartedStopInEitherRouteReturnsConflictWithoutChanges(bool inSource)
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (source, destination) = await SeedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            var startedId = inSource ? source.Stops[0].Id : destination.Stops[0].Id;
            var started = await db.RouteStops.SingleAsync(stop => stop.Id == startedId);
            started.DeliveryStatus = DeliveryStatus.Departed;
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync(factory);

        var response = await MoveAsync(client, source.Id, source.Stops[1].Id, destination.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Pending", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task LastStopInSourceReturnsConflictWithoutChanges()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (source, destination) = await SeedAsync(factory, sourceCount: 1);
        var before = await SnapshotAsync(factory);

        var response = await MoveAsync(client, source.Id, source.Stops[0].Id, destination.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Ultima oprire", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task InvalidAndMissingIdsHaveDistinctResponses()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (source, destination) = await SeedAsync(factory);
        var before = await SnapshotAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await MoveAsync(client, source.Id, source.Stops[0].Id, source.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await MoveAsync(client, source.Id, source.Stops[0].Id, Guid.Empty)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await MoveAsync(client, Guid.NewGuid(), source.Stops[0].Id, destination.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await MoveAsync(client, source.Id, Guid.NewGuid(), destination.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await MoveAsync(client, source.Id, destination.Stops[0].Id, destination.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await MoveAsync(client, source.Id, source.Stops[0].Id, Guid.NewGuid())).StatusCode);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackBothRoutes()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = CreateFactory(connection);
        using var client = factory.CreateClient();
        var (source, destination) = await SeedAsync(factory);
        var before = await SnapshotAsync(factory);
        await using (var db = new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>()
                         .UseSqlite(connection).Options))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_move
                BEFORE UPDATE OF RouteId ON RouteStops
                BEGIN SELECT RAISE(ABORT, 'simulated move failure'); END;
                """);
        }

        var response = await MoveAsync(client, source.Id, source.Stops[1].Id, destination.Id);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(factory));
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, Guid sourceRouteId,
        Guid stopId, Guid destinationRouteId) => client.PostAsJsonAsync(
        $"/api/routes/{sourceRouteId}/stops/{stopId}/move", new MoveRouteStopRequest(destinationRouteId));

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        var stops = await db.RouteStops.AsNoTracking().OrderBy(stop => stop.Id).ToListAsync();
        return string.Join('|', stops.Select(stop =>
            $"{stop.Id}:{stop.RouteId}:{stop.OrderId}:{stop.Sequence}:{stop.DeliveryStatus}:{stop.EstimatedArrival}"));
    }

    private static async Task<(Route source, Route destination)> SeedAsync(WebApplicationFactory<Program> factory,
        int sourceCount = 3, decimal destinationCapacity = 10, DateOnly? destinationDay = null,
        string destinationZone = "Nord")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        var day = new DateOnly(2026, 10, 1);
        var source = NewRoute(day, "SOURCE", 20);
        var destination = NewRoute(destinationDay ?? day, "DEST", destinationCapacity);
        for (var index = 1; index <= sourceCount; index++)
            source.Stops.Add(NewStop(index, day, "Nord"));
        for (var index = 1; index <= 2; index++)
            destination.Stops.Add(NewStop(index, destination.Date, destinationZone));
        db.Routes.AddRange(source, destination);
        await db.SaveChangesAsync();
        return (source, destination);
    }

    private static Route NewRoute(DateOnly day, string registration, decimal capacity) => new()
    {
        Id = Guid.NewGuid(), Date = day,
        Vehicle = new Vehicle { Id = Guid.NewGuid(), RegistrationNumber = registration, Capacity = capacity },
        Driver = new Driver { Id = Guid.NewGuid(), FullName = $"Șofer {registration}" }
    };

    private static RouteStop NewStop(int sequence, DateOnly day, string zone) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence,
        EstimatedArrival = new DateTimeOffset(2026, 10, 1, 10 + sequence, 0, 0, TimeSpan.Zero),
        Order = new Order
        {
            Id = Guid.NewGuid(), DeliveryDate = day, Zone = zone, Address = $"Adresa {zone} {sequence}",
            Latitude = 47, Longitude = 28 + sequence, Volume = sequence, Status = OrderStatus.Planned
        }
    };

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
