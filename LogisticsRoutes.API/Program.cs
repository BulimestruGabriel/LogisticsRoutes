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

app.Run();
