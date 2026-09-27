using System.Globalization;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MortgageRateTracker;

public sealed record RateSource(
    string Name, string Url, string AprHeading, Func<Task<MortgageRateSnapshot>> Fetch);

public sealed class DailyRateTracker(
    DailyRateHistory history,
    IReadOnlyList<RateSource> sources,
    TimeProvider clock,
    TextWriter output,
    TextWriter error,
    Func<DateOnly, Task<int>>? sundayReport = null)
{
    public async Task<int> RunAsync()
    {
        try
        {
            using var storageLock = history.AcquireLock();
            // The run belongs to the local calendar day on which it started.
            var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
            var imported = await history.InitializeAsync();
            if (imported > 0)
                await output.WriteLineAsync($"Imported {imported} daily record(s) from {history.LegacyJsonPath} into {history.FilePath}.");
            if (await history.ContainsDateAsync(today))
            {
                var detail = imported > 0
                    ? "Today's saved rates were imported; no rates were fetched."
                    : "No rates were fetched; today's saved rates were not changed.";
                await output.WriteLineAsync($"The app has already run for {today:yyyy-MM-dd} ({clock.LocalTimeZone.Id}). {detail}");
                return sundayReport is null ? 0 : await sundayReport(today);
            }

            var snapshots = new List<SourceRateSnapshot>();
            foreach (var source in sources)
            {
                await output.WriteLineAsync(source.Name);
                await output.WriteLineAsync($"Source: {source.Url}");
                try
                {
                    var snapshot = await source.Fetch();
                    var retrievedAt = clock.GetUtcNow();
                    snapshots.Add(new SourceRateSnapshot(source.Name, source.Url, retrievedAt, snapshot));
                    await output.WriteLineAsync($"Retrieved: {retrievedAt:yyyy-MM-dd HH:mm:ss} UTC");
                    await output.WriteLineAsync($"Effective as of: {snapshot.EffectiveAsOf ?? "Not provided on the page"}");
                    await output.WriteLineAsync();
                    await output.WriteLineAsync($"{"Product",-18} {"Interest Rate",15} {"Discount Points",17} {source.AprHeading,17}");
                    await output.WriteLineAsync(new string('-', 70));
                    foreach (var rate in snapshot.Rates)
                        await output.WriteLineAsync($"{rate.Product,-18} {Percent(rate.InterestRate),15} {Percent(rate.DiscountPoints),17} {Percent(rate.Apr),17}");
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or InvalidDataException or RegexMatchTimeoutException)
                {
                    await error.WriteLineAsync($"Unable to retrieve {source.Name}: {exception.Message}");
                }
                await output.WriteLineAsync();
            }

            if (snapshots.Count != sources.Count)
            {
                await error.WriteLineAsync("Today's rates were not saved because a source failed. The history is unchanged; run the app again to retry.");
                return 1;
            }

            await history.AddAsync(new DailyRateRecord(today, clock.LocalTimeZone.Id, snapshots));
            await output.WriteLineAsync($"Saved rates for {today:yyyy-MM-dd} to {history.FilePath}");
            return sundayReport is null ? 0 : await sundayReport(today);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or SqliteException)
        {
            await error.WriteLineAsync($"Unable to use rate history '{history.FilePath}': {exception.Message}");
            await error.WriteLineAsync("Check the database and permissions, and make sure another instance is not running. No daily record was saved.");
            return 1;
        }
    }

    private static string Percent(decimal? value) =>
        value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) + "%" : "Not provided";
}
