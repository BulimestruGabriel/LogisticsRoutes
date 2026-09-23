using System.ComponentModel.DataAnnotations;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.BusinessLayer.Services;

public class RouteQueryService(LogisticsDbContext db)
{
    public async Task<List<RouteResponse>> GetByDayAsync(DateOnly day, CancellationToken cancellationToken)
    {
        if (day == default)
            throw new ValidationException("Day is required.");

        var routes = await WithDetails().Where(route => route.Date == day)
            .OrderBy(route => route.Id)
            .ToListAsync(cancellationToken);
        return routes.Select(ToResponse).ToList();
    }

    public async Task<RouteResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var route = await WithDetails().SingleOrDefaultAsync(route => route.Id == id, cancellationToken);
        return route is null ? null : ToResponse(route);
    }

    private IQueryable<Route> WithDetails() => db.Routes.AsNoTracking()
        .Include(route => route.Vehicle)
        .Include(route => route.Driver)
        .Include(route => route.Stops)
        .ThenInclude(stop => stop.Order);

    private static RouteResponse ToResponse(Route route)
    {
        var stops = route.Stops.OrderBy(stop => stop.Sequence)
            .Select(stop => new RouteStopResponse(
                stop.Id,
                stop.Sequence,
                stop.DeliveryStatus.ToString(),
                stop.EstimatedArrival,
                stop.OrderId,
                stop.Order!.Zone,
                stop.Order.Address,
                stop.Order.Latitude,
                stop.Order.Longitude,
                stop.Order.Volume))
            .ToList();

        return new RouteResponse(
            route.Id,
            route.Date,
            new RouteVehicleResponse(route.VehicleId, route.Vehicle!.RegistrationNumber,
                route.Vehicle.Capacity),
            new RouteDriverResponse(route.DriverId, route.Driver!.FullName),
            stops.Sum(stop => stop.Volume),
            stops);
    }
}
