using System.ComponentModel.DataAnnotations;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.BusinessLayer.Services;

public class DriverService(LogisticsDbContext db)
{
    public async Task<DriverResponse> CreateAsync(CreateDriverRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
            throw new ValidationException("FullName is required.");

        var driver = new Driver { Id = Guid.NewGuid(), FullName = request.FullName.Trim() };
        db.Drivers.Add(driver);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(driver);
    }

    public async Task<List<DriverResponse>> GetAllAsync(CancellationToken cancellationToken)
    {
        var drivers = await db.Drivers.AsNoTracking().OrderBy(d => d.FullName)
            .ToListAsync(cancellationToken);
        return drivers.Select(ToResponse).ToList();
    }

    private static DriverResponse ToResponse(Driver driver) => new(driver.Id, driver.FullName);
}
