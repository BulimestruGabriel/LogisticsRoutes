namespace LogisticsRoutes.BusinessLayer.Models;

public record CreateVehicleRequest(string? RegistrationNumber, decimal Capacity);

public record VehicleResponse(Guid Id, string RegistrationNumber, decimal Capacity);
