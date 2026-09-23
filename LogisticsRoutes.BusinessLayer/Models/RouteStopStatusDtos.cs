namespace LogisticsRoutes.BusinessLayer.Models;

public record UpdateRouteStopStatusRequest(string? Status);

public record UpdateRouteStopStatusResponse(Guid StopId, string DeliveryStatus, string OrderStatus);
