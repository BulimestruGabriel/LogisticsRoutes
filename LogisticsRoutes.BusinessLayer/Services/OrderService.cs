using System.ComponentModel.DataAnnotations;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.BusinessLayer.Services;

public class OrderService(LogisticsDbContext db)
{
    public async Task<OrderResponse> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Zone))
            throw new ValidationException("Zone is required.");
        if (string.IsNullOrWhiteSpace(request.Address))
            throw new ValidationException("Address is required.");
        if (request.Latitude is null or < -90 or > 90 || !double.IsFinite(request.Latitude.Value))
            throw new ValidationException("Latitude must be between -90 and 90.");
        if (request.Longitude is null or < -180 or > 180 || !double.IsFinite(request.Longitude.Value))
            throw new ValidationException("Longitude must be between -180 and 180.");
        if (request.Volume <= 0)
            throw new ValidationException("Volume must be greater than 0.");
        if (request.DeliveryDate == default)
            throw new ValidationException("DeliveryDate is required.");

        var order = new Order
        {
            Id = Guid.NewGuid(),
            Zone = request.Zone.Trim(),
            Address = request.Address.Trim(),
            Latitude = request.Latitude.Value,
            Longitude = request.Longitude.Value,
            Volume = request.Volume,
            DeliveryDate = request.DeliveryDate,
            Status = OrderStatus.New
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(order);
    }

    public async Task<List<OrderResponse>> GetAllAsync(DateOnly? day, string? status, CancellationToken cancellationToken)
    {
        IQueryable<Order> query = db.Orders.AsNoTracking();
        if (day.HasValue)
            query = query.Where(order => order.DeliveryDate == day.Value);
        if (status is not null)
        {
            if (!Enum.GetNames<OrderStatus>().Any(name =>
                    string.Equals(name, status, StringComparison.OrdinalIgnoreCase)))
                throw new ValidationException("Status must be one of: New, Confirmed, Planned, Delivered, Cancelled.");
            var parsedStatus = Enum.Parse<OrderStatus>(status, true);
            query = query.Where(order => order.Status == parsedStatus);
        }

        var orders = await query.OrderBy(order => order.DeliveryDate).ThenBy(order => order.Id)
            .ToListAsync(cancellationToken);
        return orders.Select(ToResponse).ToList();
    }

    public async Task<OrderResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        return order is null ? null : ToResponse(order);
    }

    public async Task<OrderResponse?> ConfirmAsync(Guid id, CancellationToken cancellationToken)
    {
        var updated = await db.Orders.Where(order => order.Id == id && order.Status == OrderStatus.New)
            .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.Status, OrderStatus.Confirmed),
                cancellationToken);
        if (updated == 0)
        {
            if (await db.Orders.AnyAsync(order => order.Id == id, cancellationToken))
                throw new ValidationException("Only an order with status New can be confirmed.");
            return null;
        }

        return await GetByIdAsync(id, cancellationToken);
    }

    private static OrderResponse ToResponse(Order order) =>
        new(order.Id, order.Zone, order.Address, order.Latitude, order.Longitude,
            order.Volume, order.DeliveryDate, order.Status.ToString());
}
