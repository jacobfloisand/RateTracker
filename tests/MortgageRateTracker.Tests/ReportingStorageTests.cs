using Microsoft.Data.Sqlite;

namespace MortgageRateTracker.Tests;

public sealed class ReportingStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MortgageSeedTests", Guid.NewGuid().ToString("N"));
    private DailyRateHistory History => new(Path.Combine(directory, "rates.db"));
    private static readonly DateOnly Today = new(2026, 9, 27);

    [Fact]
    public async Task SeedingIsRepeatableAndClearingPreservesRealObservationsAndEmailHistory()
    {
        using var storageLock = History.AcquireLock();
        await History.InitializeAsync();
        await History.AddAsync(WeeklyReportTests.Day(Today));
        await History.AddAsync(WeeklyReportTests.Day(Today.AddDays(-10)));
        Assert.Equal(363, await History.SeedTestDataAsync(Today, "America/Denver"));
        Assert.Equal(0, await History.SeedTestDataAsync(Today, "America/Denver"));
        var records = await History.ReadAsync();
        Assert.Equal(365, records.Count);
        Assert.Equal(363, records.Count(record => record.IsTestData));
        Assert.All(records.Where(record => record.IsTestData), record => Assert.Equal(6, record.Sources.Sum(source => source.Snapshot.Rates.Count)));
        Assert.False(records.Single(record => record.Date == Today).IsTestData);
        await History.SetEmailDeliveryAsync(Today, "test@example.com", "Sent", "test@example.com", DateTimeOffset.UtcNow, true);
        Assert.Equal(363, await History.ClearTestDataAsync());
        Assert.Equal(0, await History.ClearTestDataAsync());
        Assert.Equal(2, (await History.ReadAsync()).Count);
        Assert.Equal("Sent", (await History.GetEmailDeliveryAsync(Today, "test@example.com"))!.Status);
    }

    [Fact]
    public async Task UpgradesVersionOneDatabasePreservingExistingDailyRates()
    {
        Directory.CreateDirectory(directory);
        using (var connection = new SqliteConnection($"Data Source={History.FilePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE DailyRuns (RunDate TEXT PRIMARY KEY, TimeZone TEXT);
                CREATE TABLE SourceSnapshots (RunDate TEXT, SourceOrder INTEGER, Source TEXT, SourceUrl TEXT, RetrievedAt TEXT, EffectiveAsOf TEXT);
                CREATE TABLE MortgageRates (RunDate TEXT, SourceOrder INTEGER, RateOrder INTEGER, Product TEXT, InterestRate TEXT, DiscountPoints TEXT, Apr TEXT);
                INSERT INTO DailyRuns VALUES ('2026-09-27', 'America/Denver');
                INSERT INTO SourceSnapshots VALUES ('2026-09-27', 0, 'Source', 'https://example.com', '2026-09-27 14:00:00+00:00', 'September 25');
                INSERT INTO MortgageRates VALUES ('2026-09-27', 0, 0, '15 Year Fixed', '6.375', NULL, '6.5');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }
        using var storageLock = History.AcquireLock();
        await History.InitializeAsync();
        var record = Assert.Single(await History.ReadAsync());
        Assert.False(record.IsTestData);
        Assert.Equal(6.375m, record.Sources[0].Snapshot.Rates[0].InterestRate);
        var before = await File.ReadAllBytesAsync(History.FilePath);
        await History.InitializeAsync();
        Assert.Equal(before, await File.ReadAllBytesAsync(History.FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
