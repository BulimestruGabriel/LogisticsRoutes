namespace LogisticsRoutes.Domain.Entities;

public class Vehicle
{
    public Guid Id { get; set; }
    public required string RegistrationNumber { get; set; }
    public decimal Capacity { get; set; }
}