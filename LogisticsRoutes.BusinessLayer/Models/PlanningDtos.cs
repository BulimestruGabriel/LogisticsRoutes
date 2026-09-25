namespace LogisticsRoutes.BusinessLayer.Models;

public record PlanRoutesRequest(DateOnly Day);

public record PlanRemainingRequest(DateOnly Day, List<Guid>? OrderIds = null);

public record PlannedStopResponse(Guid OrderId, int Sequence, string Address);

public record PlannedRouteResponse(
    Guid Id,
    string Zone,
    Guid VehicleId,
    Guid DriverId,
    decimal TotalVolume,
    List<PlannedStopResponse> Stops);

public record PlanRoutesResponse(DateOnly Day, List<PlannedRouteResponse> Routes);

public record ExtendedRouteResponse(Guid RouteId, List<PlannedStopResponse> AddedStops);

public record PlanRemainingResponse(DateOnly Day, List<PlannedRouteResponse> CreatedRoutes,
    List<ExtendedRouteResponse> ExtendedRoutes);
