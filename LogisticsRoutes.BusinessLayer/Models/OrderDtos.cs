namespace LogisticsRoutes.BusinessLayer.Models;

public record CreateOrderRequest(
    string? Zone,
    string? Address,
    double? Latitude,
    double? Longitude,
    decimal Volume,
    DateOnly DeliveryDate);

public record OrderResponse(
    Guid Id,
    string Zone,
    string Address,
    double Latitude,
    double Longitude,
    decimal Volume,
    DateOnly DeliveryDate,
    string Status,
    Guid? SourceOrderId);

public record UpdateOrderVolumeRequest(decimal Volume);

public record CorrectConfirmedVolumeRequest(decimal Volume, decimal ExpectedVolume);

public record CopyYesterdayRequest(DateOnly Day, List<Guid>? SourceOrderIds);

public record CopyYesterdayCandidate(
    OrderResponse SourceOrder,
    string? DeliveryStatus,
    bool WarnDeliveryOutcome,
    Guid? AlreadyCopiedOrderId,
    List<OrderResponse> PossibleExistingToday);

public record CopyYesterdayPreview(DateOnly Day, DateOnly SourceDay, List<CopyYesterdayCandidate> Candidates);

public record CopyYesterdayResult(DateOnly Day, List<OrderResponse> Created, List<OrderResponse> AlreadyCopied);
