namespace Platform.Shared.Contracts;

public sealed record EdgeBackfillCursor(string ObjectName, string? LastId, long? LastTimeUnix);

public sealed record EdgeBackfillPlan(int SourceEpoch, IReadOnlyList<EdgeBackfillCursor> Cursors);

public sealed record EdgeCheckpointRequest(long FencingToken, string ObjectName, string LastId, long? LastTimeUnix);

public sealed record EdgeEpochResetRequest(long FencingToken, int ExpectedEpoch, string ObjectName);

public sealed record EdgeEpochResetResponse(EdgeBackfillPlan Plan);

public sealed record BackfillObjectReport(string ObjectName, int RowsRead, int Rejected, string? LastId, string? Failure);

/// <param name="HasMore">O limite do comando foi atingido; a central agenda a continuacao.</param>
public sealed record BackfillReport(
    int SourceEpoch,
    bool EpochReset,
    bool HasMore,
    long? ClockSkewSeconds,
    IReadOnlyList<BackfillObjectReport> Objects);

public static class EdgeBackfillObjects
{
    /// <summary>Campo de hora de cada objeto, usado para reconhecer se a linha do cursor ainda e a mesma.</summary>
    public static readonly IReadOnlyDictionary<string, string> TimeField = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EdgeEventFamilies.AccessLogs] = "time",
        [EdgeEventFamilies.AlarmLogs] = "time",
        [EdgeEventFamilies.AccessEvents] = "timestamp",
    };
}
