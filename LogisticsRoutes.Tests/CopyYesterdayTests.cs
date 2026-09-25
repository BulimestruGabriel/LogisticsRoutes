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

public class CopyYesterdayTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    [Fact]
    public async Task ApiCorrectsOnlyUnassignedConfirmedOrderUsingExpectedVolume()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = Factory(connection);
        using var client = factory.CreateClient();
        var order = NewOrder("Centru", "Magazin", OrderStatus.Confirmed, Today);
        order.Volume = 13;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            db.Orders.Add(order);
            db.Vehicles.Add(new Vehicle { Id = Guid.NewGuid(), RegistrationNumber = "MAX-12", Capacity = 12.5m });
            await db.SaveChangesAsync();
        }

        var path = $"/api/orders/{order.Id}/confirmed-volume";
        var tooLarge = await client.PatchAsJsonAsync(path, new CorrectConfirmedVolumeRequest(13, 13));
        Assert.Equal(HttpStatusCode.Conflict, tooLarge.StatusCode);
        var corrected = await client.PatchAsJsonAsync(path, new CorrectConfirmedVolumeRequest(5, 13));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(5, (await corrected.Content.ReadFromJsonAsync<OrderResponse>())!.Volume);
        var stale = await client.PatchAsJsonAsync(path, new CorrectConfirmedVolumeRequest(4, 13));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var checkScope = factory.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        Assert.Equal(5, (await check.Orders.SingleAsync(item => item.Id == order.Id)).Volume);
    }

    [Fact]
    public async Task PreviewFlagsPossibleExistingOrdersAndDeliveryOutcomeWithoutExcludingSources()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var confirmed = NewOrder("Centru", "Magazin A", OrderStatus.Confirmed);
        var refused = NewOrder("Centru", "Magazin B", OrderStatus.Planned);
        var partial = NewOrder("Centru", "Magazin E", OrderStatus.Planned);
        var delivered = NewOrder("Sud", "Magazin C", OrderStatus.Delivered);
        var newYesterday = NewOrder("Sud", "Magazin D", OrderStatus.New);
        var existingToday = NewOrder("Centru", "  magazin   a ", OrderStatus.New, Today);
        var route = new Route
        {
            Id = Guid.NewGuid(), Date = Today.AddDays(-1),
            Vehicle = new Vehicle { Id = Guid.NewGuid(), RegistrationNumber = "TEST", Capacity = 10 },
            Driver = new Driver { Id = Guid.NewGuid(), FullName = "Șofer test" },
            Stops = [new RouteStop { Id = Guid.NewGuid(), Order = refused, Sequence = 1,
                DeliveryStatus = DeliveryStatus.Refused },
                new RouteStop { Id = Guid.NewGuid(), Order = partial, Sequence = 2,
                    DeliveryStatus = DeliveryStatus.PartialReturn }]
        };
        db.Orders.AddRange(confirmed, delivered, newYesterday, existingToday);
        db.Routes.Add(route);
        await db.SaveChangesAsync();

        var preview = await new BusinessLayer.Services.OrderService(db)
            .PreviewYesterdayAsync(Today, CancellationToken.None);

        Assert.Equal(4, preview.Candidates.Count);
        Assert.DoesNotContain(preview.Candidates, candidate => candidate.SourceOrder.Id == newYesterday.Id);
        var match = Assert.Single(preview.Candidates.Single(candidate => candidate.SourceOrder.Id == confirmed.Id)
            .PossibleExistingToday);
        Assert.Equal(existingToday.Id, match.Id);
        var warning = preview.Candidates.Single(candidate => candidate.SourceOrder.Id == refused.Id);
        Assert.True(warning.WarnDeliveryOutcome);
        Assert.Equal("Refused", warning.DeliveryStatus);
        var partialWarning = preview.Candidates.Single(candidate => candidate.SourceOrder.Id == partial.Id);
        Assert.True(partialWarning.WarnDeliveryOutcome);
        Assert.Equal("PartialReturn", partialWarning.DeliveryStatus);
        Assert.False(preview.Candidates.Single(candidate => candidate.SourceOrder.Id == delivered.Id).WarnDeliveryOutcome);
        Assert.Equal(OrderStatus.Planned, refused.Status);
    }

    [Fact]
    public async Task ApiCopyIsRepeatableAndVolumeCanChangeOnlyBeforeConfirmation()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = Factory(connection);
        using var client = factory.CreateClient();
        var source = NewOrder("Nord", "Magazin A", OrderStatus.Planned);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            db.Orders.Add(source);
            db.Vehicles.Add(new Vehicle
            {
                Id = Guid.NewGuid(), RegistrationNumber = "COPY-TEST", Capacity = 10
            });
            await db.SaveChangesAsync();
        }

        var request = new CopyYesterdayRequest(Today, [source.Id]);
        var firstResponse = await client.PostAsJsonAsync("/api/orders/copy-yesterday", request);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = (await firstResponse.Content.ReadFromJsonAsync<CopyYesterdayResult>())!;
        var copy = Assert.Single(first.Created);
        Assert.Empty(first.AlreadyCopied);
        Assert.Equal(OrderStatus.New.ToString(), copy.Status);
        Assert.Equal(source.Id, copy.SourceOrderId);
        Assert.Equal(Today, copy.DeliveryDate);
        Assert.Equal(source.Zone, copy.Zone);
        Assert.Equal(source.Address, copy.Address);
        Assert.Equal(source.Volume, copy.Volume);

        var repeated = await client.PostAsJsonAsync("/api/orders/copy-yesterday", request);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var again = (await repeated.Content.ReadFromJsonAsync<CopyYesterdayResult>())!;
        Assert.Empty(again.Created);
        Assert.Equal(copy.Id, Assert.Single(again.AlreadyCopied).Id);

        var volume = await client.PatchAsJsonAsync($"/api/orders/{copy.Id}/volume",
            new UpdateOrderVolumeRequest(3.125m));
        Assert.Equal(HttpStatusCode.OK, volume.StatusCode);
        Assert.Equal(3.125m, (await volume.Content.ReadFromJsonAsync<OrderResponse>())!.Volume);
        var confirm = await client.PatchAsync($"/api/orders/{copy.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var conflict = await client.PatchAsJsonAsync($"/api/orders/{copy.Id}/volume",
            new UpdateOrderVolumeRequest(4));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var checkScope = factory.Services.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        Assert.Equal(2, await check.Orders.CountAsync());
        Assert.Equal(OrderStatus.Planned, (await check.Orders.SingleAsync(order => order.Id == source.Id)).Status);
    }

    [Fact]
    public async Task InvalidSourceOrDuplicateSelectionReturnsConflictOrValidationWithoutWriting()
    {
        await using var connection = await OpenDatabaseAsync();
        using var factory = Factory(connection);
        using var client = factory.CreateClient();
        var ineligible = NewOrder("Centru", "Magazin", OrderStatus.New);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            db.Orders.Add(ineligible);
            await db.SaveChangesAsync();
        }

        var conflict = await client.PostAsJsonAsync("/api/orders/copy-yesterday",
            new CopyYesterdayRequest(Today, [ineligible.Id]));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var wrongDay = await client.PostAsJsonAsync("/api/orders/copy-yesterday",
            new CopyYesterdayRequest(Today.AddDays(1), [ineligible.Id]));
        Assert.Equal(HttpStatusCode.Conflict, wrongDay.StatusCode);
        var badRequest = await client.PostAsJsonAsync("/api/orders/copy-yesterday",
            new CopyYesterdayRequest(Today, [ineligible.Id, ineligible.Id]));
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        using var checkScope = factory.Services.CreateScope();
        Assert.Equal(1, await checkScope.ServiceProvider.GetRequiredService<LogisticsDbContext>()
            .Orders.CountAsync());
    }

    [Fact]
    public async Task DatabaseRejectsTwoCopiesOfSameSourceAndDeliveryDay()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var source = NewOrder("Nord", "Magazin", OrderStatus.Confirmed);
        db.Orders.Add(source);
        await db.SaveChangesAsync();

        db.Orders.AddRange(NewCopy(source), NewCopy(source));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Orders.CountAsync(order => order.SourceOrderId == source.Id));
    }

    private static Order NewCopy(Order source) => new()
    {
        Id = Guid.NewGuid(), Zone = source.Zone, Address = source.Address,
        Latitude = source.Latitude, Longitude = source.Longitude, Volume = source.Volume,
        DeliveryDate = Today, Status = OrderStatus.New, SourceOrderId = source.Id
    };

    private static Order NewOrder(string zone, string address, OrderStatus status, DateOnly? day = null) => new()
    {
        Id = Guid.NewGuid(), Zone = zone, Address = address, Latitude = 47, Longitude = 28.8,
        Volume = 2, DeliveryDate = day ?? Today.AddDays(-1), Status = status
    };

    private static WebApplicationFactory<Program> Factory(SqliteConnection connection) =>
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
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static LogisticsDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<LogisticsDbContext>().UseSqlite(connection).Options);
}
