namespace LogisticsRoutes.Domain.Entities;

public class Route
{
    public Guid Id { get; set; }
    public DateOnly Date { get; set; }
    public Guid VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }
    public Guid DriverId { get; set; }
    public Driver? Driver { get; set; }
    public List<RouteStop> Stops { get; set; } = [];
}
