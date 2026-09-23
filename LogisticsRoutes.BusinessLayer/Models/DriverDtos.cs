namespace LogisticsRoutes.BusinessLayer.Models;

public record CreateDriverRequest(string? FullName);

public record DriverResponse(Guid Id, string FullName);
