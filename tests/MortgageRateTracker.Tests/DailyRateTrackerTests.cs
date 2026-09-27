using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MortgageRateTracker.Tests;

public sealed class DailyRateTrackerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MortgageRateTrackerTests", Guid.NewGuid().ToString("N"));
    private readonly TestClock clock = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private int fetches;
    private bool fail;

    private DailyRateHistory History => new(Path.Combine(directory, "rates.db"));

    private DailyRateTracker Tracker()
    {
        RateSource[] sources =
        [
            new("America First", AmericaFirstRateSource.SourceUrl, "APR", Fetch),
            new("City Creek", CityCreekRateSource.SourceUrl, "APR", Fetch)
        ];
        return new DailyRateTracker(History, sources, clock, output, error);
    }

    private Task<MortgageRateSnapshot> Fetch()
    {
        fetches++;
        if (fail)
            throw new HttpRequestException("Test outage");
        return Task.FromResult(new MortgageRateSnapshot(
            [new MortgageRate("15 Year Fixed", 6.375m, null, 6.5m)], "September 25, 2026"));
    }

    [Fact]
    public async Task FirstRunSavesRatesAndSourceMetadata()
    {
        Assert.Equal(0, await Tracker().RunAsync());
        Assert.Equal(2, fetches);
        var record = Assert.Single(await History.ReadAsync());
        Assert.Equal(new DateOnly(2026, 9, 27), record.Date);
        Assert.Equal(clock.LocalTimeZone.Id, record.TimeZone);
        Assert.Equal(2, record.Sources.Count);
        Assert.Equal(AmericaFirstRateSource.SourceUrl, record.Sources[0].SourceUrl);
        Assert.Equal(clock.GetUtcNow(), record.Sources[0].RetrievedAt);
        Assert.Equal("September 25, 2026", record.Sources[0].Snapshot.EffectiveAsOf);
        var rate = Assert.Single(record.Sources[0].Snapshot.Rates);
        Assert.Equal(6.375m, rate.InterestRate);
        Assert.Equal(6.5m, rate.Apr);
        Assert.Null(rate.DiscountPoints);
        Assert.Contains("Saved rates for 2026-09-27", output.ToString());
    }

    [Fact]
    public async Task RepeatDayDoesNotFetchOrChangeFileEvenWithANewTrackerInstance()
    {
        Assert.Equal(0, await Tracker().RunAsync());
        var before = await File.ReadAllBytesAsync(History.FilePath);
        var modified = File.GetLastWriteTimeUtc(History.FilePath);
        clock.UtcNow = clock.UtcNow.AddHours(5);
        fail = true;

        Assert.Equal(0, await Tracker().RunAsync());

        Assert.Equal(2, fetches);
        Assert.Equal(before, await File.ReadAllBytesAsync(History.FilePath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(History.FilePath));
        Assert.Contains("already run for 2026-09-27", output.ToString());
    }

    [Fact]
    public async Task NextDayAppendsEvenWhenPublishedRatesAreUnchanged()
    {
        Assert.Equal(0, await Tracker().RunAsync());
        clock.UtcNow = clock.UtcNow.AddDays(1);
        Assert.Equal(0, await Tracker().RunAsync());
        var records = await History.ReadAsync();
        Assert.Equal(4, fetches);
        Assert.Equal(new[] { new DateOnly(2026, 9, 27), new DateOnly(2026, 9, 28) }, records.Select(record => record.Date));
    }

    [Fact]
    public async Task UsesLocalCalendarDayRatherThanUtcDate()
    {
        clock.UtcNow = new DateTimeOffset(2026, 9, 28, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, await Tracker().RunAsync());
        Assert.Equal(new DateOnly(2026, 9, 27), Assert.Single(await History.ReadAsync()).Date);
        clock.UtcNow = clock.UtcNow.AddHours(6);
        Assert.Equal(0, await Tracker().RunAsync());
        Assert.Equal(2, (await History.ReadAsync()).Count);
    }

    [Fact]
    public async Task FailedRunPreservesHistoryAndCanBeRetried()
    {
        Assert.Equal(0, await Tracker().RunAsync());
        var before = await File.ReadAllBytesAsync(History.FilePath);
        clock.UtcNow = clock.UtcNow.AddDays(1);
        fail = true;
        Assert.Equal(1, await Tracker().RunAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(History.FilePath));
        fail = false;
        Assert.Equal(0, await Tracker().RunAsync());
        Assert.Equal(2, (await History.ReadAsync()).Count);
    }

    [Fact]
    public async Task PartialSuccessDoesNotMarkDayComplete()
    {
        RateSource[] sources =
        [
            new("America First", AmericaFirstRateSource.SourceUrl, "APR", Fetch),
            new("City Creek", CityCreekRateSource.SourceUrl, "APR", () => throw new HttpRequestException("Test outage"))
        ];
        Assert.Equal(1, await new DailyRateTracker(History, sources, clock, output, error).RunAsync());
        Assert.Empty(await History.ReadAsync());
        Assert.Equal(0, await Tracker().RunAsync());
        Assert.Single(await History.ReadAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[{}]")]
    [InlineData("[{\"date\":\"2026-09-27\",\"timeZone\":\"UTC\",\"sources\":[]}]")]
    public async Task InvalidHistoryIsNotOverwrittenAndPreventsFetching(string contents)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(History.FilePath, contents);
        Assert.Equal(1, await Tracker().RunAsync());
        Assert.Equal(0, fetches);
        Assert.Equal(contents, await File.ReadAllTextAsync(History.FilePath));
    }

    [Fact]
    public async Task OverlappingRunDoesNotFetchWhileHistoryIsLocked()
    {
        using (History.AcquireLock())
        {
            Assert.Equal(1, await Tracker().RunAsync());
            Assert.Equal(0, fetches);
            Assert.False(File.Exists(History.FilePath));
        }
        Assert.Equal(0, await Tracker().RunAsync());
    }

    [Fact]
    public async Task SaveFailureDoesNotMarkDayComplete()
    {
        Directory.CreateDirectory(History.FilePath);
        Assert.Equal(1, await Tracker().RunAsync());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Directory.Delete(History.FilePath);
        Assert.Equal(0, await Tracker().RunAsync());
        Assert.Single(await History.ReadAsync());
    }

    [Fact]
    public async Task ImportsLegacyJsonOncePreservingValuesAndSkippingTodaysFetch()
    {
        Directory.CreateDirectory(directory);
        var record = SampleRecord();
        var json = JsonSerializer.Serialize(new[] { record }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(History.LegacyJsonPath, json);

        Assert.Equal(0, await Tracker().RunAsync());
        Assert.Equal(0, fetches);
        var imported = Assert.Single(await History.ReadAsync());
        Assert.Equal(JsonSerializer.Serialize(record), JsonSerializer.Serialize(imported));
        Assert.Equal(json, await File.ReadAllTextAsync(History.LegacyJsonPath));
        Assert.StartsWith("SQLite format 3", System.Text.Encoding.ASCII.GetString(
            (await File.ReadAllBytesAsync(History.FilePath))[..16]));
        Assert.Contains("Imported 1 daily record", output.ToString());

        // A changed backup must not be reimported after the database is initialized.
        await File.WriteAllTextAsync(History.LegacyJsonPath, "not json");
        var before = await File.ReadAllBytesAsync(History.FilePath);
        Assert.Equal(0, await Tracker().RunAsync());
        Assert.Equal(0, fetches);
        Assert.Equal(before, await File.ReadAllBytesAsync(History.FilePath));
        Assert.Single(await History.ReadAsync());
    }

    [Fact]
    public async Task DuplicateDayIsRejectedWithoutChangingSavedValues()
    {
        using var storageLock = History.AcquireLock();
        await History.InitializeAsync();
        await History.AddAsync(SampleRecord());
        var before = await File.ReadAllBytesAsync(History.FilePath);
        await Assert.ThrowsAsync<SqliteException>(() => History.AddAsync(SampleRecord()));
        Assert.Equal(before, await File.ReadAllBytesAsync(History.FilePath));
        Assert.Single(await History.ReadAsync());
    }

    [Fact]
    public async Task FailedInsertRollsBackDailyRunSourcesAndRates()
    {
        using var storageLock = History.AcquireLock();
        await History.InitializeAsync();
        var record = SampleRecord();
        var source = record.Sources[1];
        var invalid = record with
        {
            Sources = [record.Sources[0], source with
            {
                Snapshot = source.Snapshot with
                {
                    Rates = [source.Snapshot.Rates[0], source.Snapshot.Rates[0]]
                }
            }]
        };
        await Assert.ThrowsAsync<SqliteException>(() => History.AddAsync(invalid));
        Assert.False(await History.ContainsDateAsync(record.Date));
        using var connection = new SqliteConnection($"Data Source={History.FilePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM DailyRuns) + (SELECT COUNT(*) FROM SourceSnapshots) + (SELECT COUNT(*) FROM MortgageRates);";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        await History.AddAsync(record);
        Assert.Single(await History.ReadAsync());
    }

    [Fact]
    public async Task FailedMigrationRollsBackAllRecordsAndCanBeRetried()
    {
        Directory.CreateDirectory(directory);
        var record = SampleRecord();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(new[] { record, record }, options);
        await File.WriteAllTextAsync(History.LegacyJsonPath, json);
        Assert.Equal(1, await Tracker().RunAsync());
        Assert.Equal(0, fetches);
        Assert.Equal(json, await File.ReadAllTextAsync(History.LegacyJsonPath));
        using (var connection = new SqliteConnection($"Data Source={History.FilePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        await File.WriteAllTextAsync(History.LegacyJsonPath, JsonSerializer.Serialize(new[] { record }, options));
        Assert.Equal(0, await Tracker().RunAsync());
        Assert.Equal(0, fetches);
        Assert.Single(await History.ReadAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[{}]")]
    public async Task InvalidLegacyJsonPreventsFetchingAndIsPreserved(string json)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(History.LegacyJsonPath, json);
        Assert.Equal(1, await Tracker().RunAsync());
        Assert.Equal(0, fetches);
        Assert.Equal(json, await File.ReadAllTextAsync(History.LegacyJsonPath));
    }

    private DailyRateRecord SampleRecord() => new(new DateOnly(2026, 9, 27), clock.LocalTimeZone.Id,
    [
        new SourceRateSnapshot("America First", AmericaFirstRateSource.SourceUrl, clock.GetUtcNow(),
            new MortgageRateSnapshot([new MortgageRate("15 Year Fixed", 6.1234567890123456789012345678m, 0.375m, 6.7m)], "September 25, 2026")),
        new SourceRateSnapshot("City Creek", CityCreekRateSource.SourceUrl, clock.GetUtcNow(),
            new MortgageRateSnapshot([new MortgageRate("15 Year Fixed", 6.375m, null, 6.5m)], null))
    ]);

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 27, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("Test Mountain", TimeSpan.FromHours(-6), "Test Mountain", "Test Mountain");
    }
}
