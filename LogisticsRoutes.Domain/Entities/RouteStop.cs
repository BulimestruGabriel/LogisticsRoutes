namespace LogisticsRoutes.Domain.Entities;

public class RouteStop
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public Route? Route { get; set; }
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset EstimatedArrival { get; set; }
    public DeliveryStatus DeliveryStatus { get; set; } = DeliveryStatus.Pending;
}
