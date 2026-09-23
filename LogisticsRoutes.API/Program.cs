using System.ComponentModel.DataAnnotations;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.BusinessLayer.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("LogisticsDb")
    ?? throw new InvalidOperationException("Connection string 'LogisticsDb' is missing.");

builder.Services.AddDbContext<LogisticsDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<VehicleService>();
builder.Services.AddScoped<DriverService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<RoutePlanningService>();
builder.Services.AddScoped<RouteQueryService>();
builder.Services.AddScoped<RouteOrderService>();
builder.Services.AddScoped<RouteStopStatusService>();
builder.Services.AddSingleton(new PlanningSettings(
    builder.Configuration.GetValue<double?>("Planning:DepotLatitude"),
    builder.Configuration.GetValue<double?>("Planning:DepotLongitude")));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (ValidationException exception)
    {
        await Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status400BadRequest)
            .ExecuteAsync(context);
    }
    catch (BadHttpRequestException exception)
    {
        await Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status400BadRequest)
            .ExecuteAsync(context);
    }
    catch (PlanningConflictException exception)
    {
        await Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status409Conflict)
            .ExecuteAsync(context);
    }
    catch (RouteStopStatusConflictException exception)
    {
        await Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status409Conflict)
            .ExecuteAsync(context);
    }
    catch (RouteOrderConflictException exception)
    {
        await Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status409Conflict)
            .ExecuteAsync(context);
    }
    catch (PlanningConfigurationException exception)
    {
        await Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError)
            .ExecuteAsync(context);
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapPost("/api/vehicles", async (CreateVehicleRequest request, VehicleService service,
        CancellationToken cancellationToken) =>
        Results.Json(await service.CreateAsync(request, cancellationToken), statusCode: StatusCodes.Status201Created))
    .WithTags("Vehicles")
    .Produces<VehicleResponse>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapGet("/api/vehicles", async (VehicleService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.GetAllAsync(cancellationToken)))
    .WithTags("Vehicles")
    .Produces<List<VehicleResponse>>();

app.MapPost("/api/drivers", async (CreateDriverRequest request, DriverService service,
        CancellationToken cancellationToken) =>
        Results.Json(await service.CreateAsync(request, cancellationToken), statusCode: StatusCodes.Status201Created))
    .WithTags("Drivers")
    .Produces<DriverResponse>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapGet("/api/drivers", async (DriverService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.GetAllAsync(cancellationToken)))
    .WithTags("Drivers")
    .Produces<List<DriverResponse>>();

app.MapPost("/api/orders", async (CreateOrderRequest request, OrderService service,
        CancellationToken cancellationToken) =>
    {
        var order = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/orders/{order.Id}", order);
    })
    .WithTags("Orders")
    .Produces<OrderResponse>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapGet("/api/orders", async (DateOnly? day, string? status, OrderService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetAllAsync(day, status, cancellationToken)))
    .WithTags("Orders")
    .Produces<List<OrderResponse>>()
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapGet("/api/orders/{id}", async (Guid id, OrderService service, CancellationToken cancellationToken) =>
    {
        var order = await service.GetByIdAsync(id, cancellationToken);
        return order is null ? Results.NotFound() : Results.Ok(order);
    })
    .WithTags("Orders")
    .Produces<OrderResponse>()
    .Produces(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapPatch("/api/orders/{id}/confirm", async (Guid id, OrderService service,
        CancellationToken cancellationToken) =>
    {
        var order = await service.ConfirmAsync(id, cancellationToken);
        return order is null ? Results.NotFound() : Results.Ok(order);
    })
    .WithTags("Orders")
    .Produces<OrderResponse>()
    .Produces(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapPost("/api/routes/plan", async (PlanRoutesRequest request, RoutePlanningService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.PlanAsync(request, cancellationToken)))
    .WithTags("Routes")
    .Produces<PlanRoutesResponse>()
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status500InternalServerError);

app.MapGet("/api/routes", async (DateOnly day, RouteQueryService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetByDayAsync(day, cancellationToken)))
    .WithTags("Routes")
    .Produces<List<RouteResponse>>()
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapGet("/api/routes/{id}", async (Guid id, RouteQueryService service,
        CancellationToken cancellationToken) =>
    {
        var route = await service.GetByIdAsync(id, cancellationToken);
        return route is null ? Results.NotFound() : Results.Ok(route);
    })
    .WithTags("Routes")
    .Produces<RouteResponse>()
    .Produces(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status400BadRequest);

app.MapPost("/api/routes/{routeId}/orders", async (Guid routeId, AddRouteOrderRequest request,
        RouteOrderService service, CancellationToken cancellationToken) =>
    {
        var route = await service.AddAsync(routeId, request, cancellationToken);
        return route is null
            ? Results.Problem(detail: "Ruta sau comanda nu a fost găsită.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(route);
    })
    .WithTags("Routes")
    .Produces<RouteResponse>()
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);

app.MapPatch("/api/routes/{routeId}/stops/{stopId}/status", async (Guid routeId, Guid stopId,
        UpdateRouteStopStatusRequest request, RouteStopStatusService service,
        CancellationToken cancellationToken) =>
    {
        var result = await service.UpdateAsync(routeId, stopId, request, cancellationToken);
        return result is null
            ? Results.Problem(detail: "Ruta sau oprirea nu a fost găsită.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(result);
    })
    .WithTags("Routes")
    .Produces<UpdateRouteStopStatusResponse>()
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);

app.Run();

public partial class Program;
