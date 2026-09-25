using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LogisticsRoutes.API;

public enum PositionWriteScope
{
    Reported,
    Simulated
}

public enum PositionTokenResult
{
    Invalid,
    WrongRouteOrScope,
    Allowed
}

public sealed class RoutePositionAccess
{
    private readonly byte[]? signingKey;

    public RoutePositionAccess(string? base64SigningKey)
    {
        if (string.IsNullOrWhiteSpace(base64SigningKey)) return;
        try
        {
            signingKey = Convert.FromBase64String(base64SigningKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("DriverAccess:SigningKey must be a base64-encoded random key.", exception);
        }

        if (signingKey.Length < 32)
            throw new InvalidOperationException("DriverAccess:SigningKey must contain at least 32 random bytes.");
    }

    public bool IsConfigured => signingKey is not null;

    public string Issue(Guid routeId, PositionWriteScope scope)
    {
        if (signingKey is null)
            throw new InvalidOperationException("DriverAccess:SigningKey is not configured.");
        if (routeId == Guid.Empty)
            throw new ArgumentException("A route ID is required.", nameof(routeId));

        var expiresAt = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();
        var payload = $"v1.{routeId:N}.{scope}.{expiresAt}";
        var signature = HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes(payload));
        return $"{payload}.{Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    public PositionTokenResult Validate(string? token, Guid routeId, PositionWriteScope requiredScope)
    {
        if (signingKey is null || string.IsNullOrWhiteSpace(token) || token.Length > 256)
            return PositionTokenResult.Invalid;

        var parts = token.Split('.');
        if (parts.Length != 5 || parts[0] != "v1" ||
            !Guid.TryParseExact(parts[1], "N", out var tokenRouteId) ||
            !Enum.TryParse<PositionWriteScope>(parts[2], false, out var tokenScope) ||
            tokenScope.ToString() != parts[2] ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt) ||
            expiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return PositionTokenResult.Invalid;

        byte[] suppliedSignature;
        try
        {
            var base64 = parts[4].Replace('-', '+').Replace('_', '/');
            suppliedSignature = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException)
        {
            return PositionTokenResult.Invalid;
        }

        var payload = string.Join('.', parts.Take(4));
        var expectedSignature = HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes(payload));
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature))
            return PositionTokenResult.Invalid;

        return tokenRouteId == routeId && tokenScope == requiredScope
            ? PositionTokenResult.Allowed : PositionTokenResult.WrongRouteOrScope;
    }
}
