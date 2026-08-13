using Microsoft.Data.Sqlite;
using Npgsql;

namespace NzbWebDAV.Setup.Core;

/// <summary>
/// The only transaction outcomes setup code is allowed to infer. In
/// particular, an exception from CommitAsync is not a rollback unless the
/// provider told us so (or a rollback operation completed successfully).
/// </summary>
internal enum SetupTransactionState
{
    Active,
    Committed,
    RolledBack,
    Unknown,
}

internal enum SetupCommitOutcome
{
    DefinitivelyAborted,
    Uncertain,
}

internal static class SetupTransactionOutcome
{
    /// <summary>
    /// PostgreSQL reports these errors after aborting the transaction. Keep
    /// this list deliberately narrow: a unique violation, a timeout, and a
    /// transport failure do not establish that a commit was rejected.
    /// </summary>
    public static bool IsPostgresDefinitiveAbort(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && postgres.SqlState is "40001" or "40P01")
                return true;
        }

        return false;
    }

    public static SetupCommitOutcome ClassifyCommitFailure(
        Exception exception,
        SetupTransactionState transactionState)
        => transactionState == SetupTransactionState.RolledBack
           || IsPostgresDefinitiveAbort(exception)
            ? SetupCommitOutcome.DefinitivelyAborted
            : SetupCommitOutcome.Uncertain;

    /// <summary>
    /// These are provider errors for which retrying is safe only after the
    /// transaction has been rolled back and disposed. Do not use messages or
    /// a general DbException catch here; commit outcomes must remain explicit.
    /// </summary>
    public static bool IsRetryableWriteAbort(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && postgres.SqlState is "40001" or "40P01")
                return true;

            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode is 5 or 6)
                return true;
        }

        return false;
    }
}

internal sealed class SetupRetryableTransactionException(Exception innerException)
    : Exception("The setup transaction was conclusively aborted and may be retried.", innerException);

/// <summary>
/// Authentication reached an outcome that cannot prove whether Jellyfin
/// created a session. The durable operation id remains the recovery identity;
/// callers must not treat this as an ordinary authentication rejection.
/// </summary>
public sealed class SetupAuthenticationRecoveryRequiredException : Exception
{
    public SetupAuthenticationRecoveryRequiredException(string operationId, Exception innerException)
        : base("Jellyfin authentication outcome is uncertain; setup session recovery is required.", innerException)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("An authentication operation id is required.", nameof(operationId));

        OperationId = operationId;
    }

    public string OperationId { get; }
}

/// <summary>
/// A transaction could not be conclusively closed. External session cleanup
/// must not run in this state because the database may still own the token.
/// </summary>
internal sealed class SetupTransactionRecoveryRequiredException : Exception
{
    public SetupTransactionRecoveryRequiredException(
        Exception operationFailure,
        Exception? rollbackFailure,
        Exception? disposeFailure)
        : base(
            "Setup persistence could not establish the database transaction outcome; " +
            "external session cleanup was not attempted.",
            operationFailure)
    {
        RollbackFailure = rollbackFailure;
        DisposeFailure = disposeFailure;
    }

    public Exception? RollbackFailure { get; }
    public Exception? DisposeFailure { get; }
}
