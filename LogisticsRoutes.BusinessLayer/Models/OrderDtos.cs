namespace LogisticsRoutes.BusinessLayer.Models;

public record CreateOrderRequest(
    string? Zone,
    string? Address,
    double? Latitude,
    double? Longitude,
    decimal Volume,
    DateOnly DeliveryDate);

public record OrderResponse(
    Guid Id,
    string Zone,
    string Address,
    double Latitude,
    double Longitude,
    decimal Volume,
    DateOnly DeliveryDate,
    string Status);
