namespace LogisticsRoutes.Domain.Entities;

public class Order
{
    public Guid Id { get; set; }
    public required string Zone { get; set; }
    public required string Address { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public decimal Volume { get; set; }
    public DateOnly DeliveryDate { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.New;
}
