using System.ComponentModel.DataAnnotations;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.BusinessLayer.Services;

public class VehicleService(LogisticsDbContext db)
{
    public async Task<VehicleResponse> CreateAsync(CreateVehicleRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RegistrationNumber))
            throw new ValidationException("RegistrationNumber is required.");
        if (request.Capacity <= 0)
            throw new ValidationException("Capacity must be greater than 0.");

        var vehicle = new Vehicle
        {
            Id = Guid.NewGuid(),
            RegistrationNumber = request.RegistrationNumber.Trim(),
            Capacity = request.Capacity
        };
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(vehicle);
    }

    public async Task<List<VehicleResponse>> GetAllAsync(CancellationToken cancellationToken)
    {
        var vehicles = await db.Vehicles.AsNoTracking().OrderBy(v => v.RegistrationNumber)
            .ToListAsync(cancellationToken);
        return vehicles.Select(ToResponse).ToList();
    }

    private static VehicleResponse ToResponse(Vehicle vehicle) =>
        new(vehicle.Id, vehicle.RegistrationNumber, vehicle.Capacity);
}
