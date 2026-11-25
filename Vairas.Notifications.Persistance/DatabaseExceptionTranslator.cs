using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vairas.Notifications.Business;

namespace Vairas.Notifications.Persistance;

/// <summary>
/// Translates Npgsql failures to application exceptions at the persistence boundary, so the API layer never classifies
/// provider exceptions or SQLSTATE values. Original exceptions are kept as inner exceptions for diagnostics.
/// </summary>
public static class DatabaseExceptionTranslator
{
    /// <summary>
    /// Constraints whose violation is an expected business outcome. Any other foreign-key or check violation is an
    /// unexpected integrity failure (see <see cref="PersistenceFailureException"/>).
    /// </summary>
    private static readonly HashSet<string> BusinessConstraints = new(StringComparer.Ordinal);

    // Only demonstrated schema constraints represent expected operation conflicts. Unknown uniqueness is an integrity defect.
    private static readonly HashSet<string> UniqueConflicts = new(StringComparer.Ordinal) { "nt_one_active" };
    private static readonly HashSet<string> ExclusionConflicts = new(StringComparer.Ordinal) {  };

    public static Exception? Translate(Exception exception)
    {
        if (exception is OperationCanceledException or BusinessException or PersistenceFailureException)
            return null;
        var postgres = exception as PostgresException ?? (exception as DbUpdateException)?.InnerException as PostgresException;
        if (postgres is not null)
            return postgres.SqlState switch
            {
                PostgresErrorCodes.UniqueViolation when postgres.ConstraintName is { } unique && UniqueConflicts.Contains(unique) =>
                    new ConflictException("RESOURCE_CONFLICT", "The command conflicts with existing state.", exception),
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable =>
                    new ServiceUnavailableException("RETRY_REQUIRED", "Retry the same operation.", exception),
                PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation when postgres.ConstraintName is { } name && BusinessConstraints.Contains(name) =>
                    new ConflictException("BUSINESS_RULE_REJECTED", "The command violates a business rule.", exception),
                _ => new PersistenceFailureException(postgres.SqlState, postgres.ConstraintName, exception)
            };
        if (exception is NpgsqlException { IsTransient: true })
            return new ServiceUnavailableException("DATABASE_UNAVAILABLE", "The database is unavailable.", exception);
        if (exception is DbUpdateException)
            return new PersistenceFailureException(null, null, exception);
        return null;
    }

    /// <summary>Runs a database operation and throws the translated exception when the failure is recognised.</summary>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (Exception e) when (Translate(e) is not null) { throw Translate(e)!; }
    }

    public static async Task GuardAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception e) when (Translate(e) is not null) { throw Translate(e)!; }
    }
}
