namespace LogisticsRoutes.BusinessLayer.Models;

public record RouteVehicleResponse(Guid Id, string RegistrationNumber);

public record RouteDriverResponse(Guid Id, string FullName);

public record RouteStopResponse(
    Guid Id,
    int Sequence,
    string DeliveryStatus,
    DateTimeOffset? EstimatedArrival,
    Guid OrderId,
    string Zone,
    string Address,
    double Latitude,
    double Longitude,
    decimal Volume);

public record RouteResponse(
    Guid Id,
    DateOnly Date,
    RouteVehicleResponse Vehicle,
    RouteDriverResponse Driver,
    decimal TotalVolume,
    List<RouteStopResponse> Stops);
