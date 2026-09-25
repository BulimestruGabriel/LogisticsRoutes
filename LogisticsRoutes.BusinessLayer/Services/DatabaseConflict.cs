using Npgsql;

namespace LogisticsRoutes.BusinessLayer.Services;

internal static class DatabaseConflict
{
    internal static bool IsConcurrent(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres && postgres.SqlState is
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation or
                PostgresErrorCodes.DeadlockDetected)
                return true;
        return false;
    }
}
