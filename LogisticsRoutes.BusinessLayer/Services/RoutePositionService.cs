using System.ComponentModel.DataAnnotations;
using System.Data;
using LogisticsRoutes.BusinessLayer.Data;
using LogisticsRoutes.BusinessLayer.Models;
using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LogisticsRoutes.BusinessLayer.Services;

public class RoutePositionService(LogisticsDbContext db)
{
    public async Task<(bool RouteExists, RoutePositionResponse? Position)> GetAsync(Guid routeId,
        CancellationToken cancellationToken)
    {
        if (!await db.Routes.AsNoTracking().AnyAsync(route => route.Id == routeId, cancellationToken))
            return (false, null);

        var position = await db.RoutePositions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RouteId == routeId, cancellationToken);
        return (true, position is null ? null : ToResponse(position));
    }

    public async Task<RoutePositionResponse?> ReportAsync(Guid routeId, ReportRoutePositionRequest request,
        CancellationToken cancellationToken)
    {
        if (routeId == Guid.Empty)
            throw new ValidationException("ID-ul rutei este obligatoriu.");
        if (request.Latitude is not { } latitude || !double.IsFinite(latitude) || latitude is < -90 or > 90 ||
            request.Longitude is not { } longitude || !double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ValidationException("Latitudinea trebuie să fie între -90 și 90, iar longitudinea între -180 și 180.");
        if (request.ReportedAt == default || request.ReportedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new ValidationException("Momentul raportării este obligatoriu și nu poate fi în viitor cu peste 5 minute.");

        var sourceName = request.Source?.Trim();
        if (sourceName is not null && !Enum.GetNames<PositionSource>().Any(name =>
                string.Equals(name, sourceName, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException("Sursa poziției trebuie să fie Reported sau Simulated.");
        var source = sourceName is null ? PositionSource.Reported : Enum.Parse<PositionSource>(sourceName, true);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            if (!await db.Routes.AnyAsync(route => route.Id == routeId, cancellationToken))
                return null;

            var position = await db.RoutePositions.SingleOrDefaultAsync(item => item.RouteId == routeId,
                cancellationToken);
            var reportedAt = request.ReportedAt.ToUniversalTime();
            if (position is not null && reportedAt < position.ReportedAt)
                throw new RoutePositionConflictException("Poziția raportată este mai veche decât ultima poziție salvată.");

            if (position is null)
            {
                position = new RoutePosition { RouteId = routeId };
                db.RoutePositions.Add(position);
            }
            position.Latitude = latitude;
            position.Longitude = longitude;
            position.ReportedAt = reportedAt;
            position.ReceivedAt = DateTimeOffset.UtcNow;
            position.Source = source;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToResponse(position);
        }
        catch (PostgresException exception) when (IsConcurrentConflict(exception))
        {
            throw new RoutePositionConflictException("Poziția rutei a fost actualizată între timp. Reîncearcă.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres &&
                                                  IsConcurrentConflict(postgres))
        {
            throw new RoutePositionConflictException("Poziția rutei a fost actualizată între timp. Reîncearcă.");
        }
    }

    private static RoutePositionResponse ToResponse(RoutePosition position) => new(
        position.RouteId, position.Latitude, position.Longitude, position.ReportedAt,
        position.ReceivedAt, position.Source.ToString());

    private static bool IsConcurrentConflict(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation;
}
