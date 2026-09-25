using System.ComponentModel.DataAnnotations;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.BusinessLayer.Services;

public class OrderService(LogisticsDbContext db)
{
    private static readonly OrderStatus[] CopyableStatuses =
        [OrderStatus.Confirmed, OrderStatus.Planned, OrderStatus.Delivered];

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
        var updated = await db.Orders.Where(order => order.Id == id && order.Status == OrderStatus.New &&
                db.Vehicles.Any(vehicle => vehicle.Capacity >= order.Volume))
            .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.Status, OrderStatus.Confirmed),
                cancellationToken);
        if (updated == 0)
        {
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id,
                cancellationToken);
            if (order is null)
                return null;
            if (order.Status != OrderStatus.New)
                throw new ValidationException("Only an order with status New can be confirmed.");
            var capacities = await db.Vehicles.AsNoTracking().Select(vehicle => vehicle.Capacity)
                .ToListAsync(cancellationToken);
            decimal? maximum = capacities.Count == 0 ? null : capacities.Max();
            throw new OrderConflictException(maximum is null
                ? "Comanda nu poate fi confirmată: nu există niciun vehicul în flotă. Adaugă un vehicul."
                : $"Comanda nu poate fi confirmată: volumul {order.Volume:0.###} depășește capacitatea maximă a unui vehicul ({maximum.Value:0.###}). Corectează volumul sau adaugă un vehicul potrivit.");
        }

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<OrderResponse?> UpdateVolumeAsync(Guid id, UpdateOrderVolumeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateVolume(request.Volume);

        var updated = await db.Orders.Where(order => order.Id == id && order.Status == OrderStatus.New)
            .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.Volume, request.Volume),
                cancellationToken);
        if (updated == 0)
        {
            if (await db.Orders.AnyAsync(order => order.Id == id, cancellationToken))
                throw new OrderConflictException("Volumul poate fi modificat doar cât comanda este New.");
            return null;
        }
        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<OrderResponse?> CorrectConfirmedVolumeAsync(Guid id, CorrectConfirmedVolumeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateVolume(request.Volume);
        ValidateVolume(request.ExpectedVolume);

        try
        {
            // The status, absence of a stop, original volume and current fleet are checked in the
            // same UPDATE. A planner also updates this order row before committing its stop.
            var updated = await db.Orders.Where(order => order.Id == id &&
                    order.Status == OrderStatus.Confirmed && order.Volume == request.ExpectedVolume &&
                    !db.RouteStops.Any(stop => stop.OrderId == order.Id) &&
                    db.Vehicles.Any(vehicle => vehicle.Capacity >= request.Volume))
                .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.Volume, request.Volume),
                    cancellationToken);
            if (updated == 1)
                return await GetByIdAsync(id, cancellationToken);
        }
        catch (Exception exception) when (DatabaseConflict.IsConcurrent(exception))
        {
            throw new OrderConflictException("Comanda a fost modificată simultan. Reîncarcă lista și încearcă din nou.");
        }

        var current = await db.Orders.AsNoTracking().SingleOrDefaultAsync(order => order.Id == id,
            cancellationToken);
        if (current is null) return null;
        if (await db.RouteStops.AnyAsync(stop => stop.OrderId == id, cancellationToken))
            throw new OrderConflictException("Comanda aparține deja unei rute; volumul nu mai poate fi corectat.");
        if (current.Status != OrderStatus.Confirmed)
            throw new OrderConflictException("Comanda nu mai este confirmată; reîncarcă lista.");
        if (current.Volume != request.ExpectedVolume)
            throw new OrderConflictException("Volumul comenzii a fost modificat între timp; reîncarcă lista.");

        var capacities = await db.Vehicles.AsNoTracking().Select(vehicle => vehicle.Capacity)
            .ToListAsync(cancellationToken);
        decimal? maximum = capacities.Count == 0 ? null : capacities.Max();
        throw new OrderConflictException(maximum is null
            ? "Nu există niciun vehicul în flotă. Adaugă un vehicul înainte de corectarea comenzii."
            : $"Volumul {request.Volume:0.###} depășește capacitatea maximă actuală a unui vehicul ({maximum.Value:0.###}). Alege un volum mai mic.");
    }

    private static void ValidateVolume(decimal volume)
    {
        if (volume <= 0 || decimal.Round(volume, 3) != volume || volume > 999999999999999.999m)
            throw new ValidationException("Volumul trebuie să fie pozitiv și să aibă cel mult 3 zecimale.");
    }

    public async Task<CopyYesterdayPreview> PreviewYesterdayAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var sourceDay = SourceDay(day);
        var sourceOrders = await db.Orders.AsNoTracking()
            .Where(order => order.DeliveryDate == sourceDay && CopyableStatuses.Contains(order.Status))
            .OrderBy(order => order.Address).ThenBy(order => order.Id).ToListAsync(cancellationToken);
        var todayOrders = await db.Orders.AsNoTracking()
            .Where(order => order.DeliveryDate == day).ToListAsync(cancellationToken);
        var sourceIds = sourceOrders.Select(order => order.Id).ToList();
        var outcomes = await db.RouteStops.AsNoTracking()
            .Where(stop => sourceIds.Contains(stop.OrderId))
            .Select(stop => new { stop.OrderId, stop.DeliveryStatus })
            .ToListAsync(cancellationToken);
        var outcomeByOrder = outcomes.ToDictionary(stop => stop.OrderId, stop => stop.DeliveryStatus);

        var candidates = sourceOrders.Select(source =>
        {
            outcomeByOrder.TryGetValue(source.Id, out var outcome);
            var copied = todayOrders.FirstOrDefault(order => order.SourceOrderId == source.Id);
            var possible = todayOrders.Where(order => order.SourceOrderId != source.Id &&
                Normalize(order.Zone) == Normalize(source.Zone) &&
                Normalize(order.Address) == Normalize(source.Address))
                .Select(ToResponse).ToList();
            return new CopyYesterdayCandidate(ToResponse(source),
                outcomeByOrder.ContainsKey(source.Id) ? outcome.ToString() : null,
                outcome is DeliveryStatus.Refused or DeliveryStatus.PartialReturn,
                copied?.Id, possible);
        }).ToList();
        return new CopyYesterdayPreview(day, sourceDay, candidates);
    }

    public async Task<CopyYesterdayResult> CopyYesterdayAsync(CopyYesterdayRequest request,
        CancellationToken cancellationToken)
    {
        var sourceDay = SourceDay(request.Day);
        var ids = request.SourceOrderIds;
        if (ids is null || ids.Count == 0 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new ValidationException("Selectează cel puțin o comandă-sursă, fără identificatori repetați.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sources = await db.Orders.AsNoTracking()
            .Where(order => ids.Contains(order.Id)).ToListAsync(cancellationToken);
        if (sources.Count != ids.Count || sources.Any(order =>
                order.DeliveryDate != sourceDay || !CopyableStatuses.Contains(order.Status)))
            throw new OrderConflictException("Selecția nu mai corespunde comenzilor eligibile de ieri. Reîncarcă previzualizarea.");

        var byId = sources.ToDictionary(order => order.Id);
        var createdIds = new HashSet<Guid>();
        foreach (var sourceId in ids)
        {
            var source = byId[sourceId];
            var newId = Guid.NewGuid();
            // Both PostgreSQL and SQLite support this conflict target. The unique index is the
            // final arbiter if two requests copy the same source at the same time.
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Orders" ("Id", "Zone", "Address", "Latitude", "Longitude", "Volume",
                    "DeliveryDate", "Status", "SourceOrderId")
                VALUES ({newId}, {source.Zone}, {source.Address}, {source.Latitude}, {source.Longitude},
                    {source.Volume}, {request.Day}, {nameof(OrderStatus.New)}, {source.Id})
                ON CONFLICT ("SourceOrderId", "DeliveryDate") DO NOTHING
                """, cancellationToken);
            if (inserted == 1) createdIds.Add(newId);
        }
        await transaction.CommitAsync(cancellationToken);

        var copies = await db.Orders.AsNoTracking()
            .Where(order => order.DeliveryDate == request.Day && order.SourceOrderId.HasValue &&
                ids.Contains(order.SourceOrderId.Value))
            .ToListAsync(cancellationToken);
        return new CopyYesterdayResult(request.Day,
            copies.Where(order => createdIds.Contains(order.Id)).Select(ToResponse).ToList(),
            copies.Where(order => !createdIds.Contains(order.Id)).Select(ToResponse).ToList());
    }

    private static DateOnly SourceDay(DateOnly day)
    {
        if (day <= DateOnly.MinValue)
            throw new ValidationException("Alege o zi de livrare validă.");
        return day.AddDays(-1);
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static OrderResponse ToResponse(Order order) =>
        new(order.Id, order.Zone, order.Address, order.Latitude, order.Longitude,
            order.Volume, order.DeliveryDate, order.Status.ToString(), order.SourceOrderId);
}

public sealed class OrderConflictException(string message) : Exception(message);
