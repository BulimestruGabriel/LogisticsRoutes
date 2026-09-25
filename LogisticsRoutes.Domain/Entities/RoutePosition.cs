namespace LogisticsRoutes.Domain.Entities;

public class RoutePosition
{
    public Guid RouteId { get; set; }
    public Route? Route { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public PositionSource Source { get; set; }
}
