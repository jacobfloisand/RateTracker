using System.Text.Json.Serialization;

namespace MortgageRateTracker;

public sealed record SourceRateSnapshot(
    [property: JsonRequired] string Source,
    [property: JsonRequired] string SourceUrl,
    [property: JsonRequired] DateTimeOffset RetrievedAt,
    [property: JsonRequired] MortgageRateSnapshot Snapshot);

public sealed record DailyRateRecord(
    [property: JsonRequired] DateOnly Date,
    [property: JsonRequired] string TimeZone,
    [property: JsonRequired] IReadOnlyList<SourceRateSnapshot> Sources,
    bool IsTestData = false);

