namespace LogisticsRoutes.BusinessLayer.Models;

public record ReportRoutePositionRequest(
    double? Latitude,
    double? Longitude,
    DateTimeOffset ReportedAt,
    string? Source);

public record RoutePositionResponse(
    Guid RouteId,
    double Latitude,
    double Longitude,
    DateTimeOffset ReportedAt,
    DateTimeOffset ReceivedAt,
    string Source);
