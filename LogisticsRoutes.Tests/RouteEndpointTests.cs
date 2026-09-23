using System.Net;
using LogisticsRoutes.BusinessLayer.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LogisticsRoutes.Tests;

public class RouteEndpointTests
{
    [Fact]
    public async Task GetMissingRouteReturns404()
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
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LogisticsDbContext>()
                .Database.EnsureCreatedAsync();
        }

        var response = await client.GetAsync($"/api/routes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
