using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MortgageRateTracker;

public sealed partial class DailyRateHistory(string filePath)
{
    public string FilePath { get; } = Path.GetFullPath(filePath);
    public string LegacyJsonPath => Path.ChangeExtension(FilePath, ".json");

    // Keep this sidecar in place: all processes must lock the same file through
    // the daily check, web requests, and database commit.
    public FileStream AcquireLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        return new FileStream(FilePath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    // Called under the application lock. Schema creation and legacy import either
    // commit together or roll back together. Existing databases only read the version.
    public async Task<int> InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var connection = OpenConnection();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = (long)(await versionCommand.ExecuteScalarAsync())!;
        if (version == 2)
            return 0;
        if (version == 1)
        {
            using var upgrade = connection.BeginTransaction();
            await UpgradeReportingAsync(connection, upgrade);
            upgrade.Commit();
            return 0;
        }
        if (version != 0)
            throw new InvalidDataException($"Unsupported rate database version: {version}.");

        using var tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        if ((long)(await tablesCommand.ExecuteScalarAsync())! != 0)
            throw new InvalidDataException("The selected database already contains an unrecognized schema.");

        var legacyRecords = new List<DailyRateRecord>();
        if (File.Exists(LegacyJsonPath))
        {
            await using var stream = File.OpenRead(LegacyJsonPath);
            legacyRecords = await JsonSerializer.DeserializeAsync<List<DailyRateRecord>>(stream,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new InvalidDataException("The legacy JSON history is null.");
            foreach (var record in legacyRecords)
                ValidateRecord(record);
        }

        using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE DailyRuns (
                RunDate TEXT NOT NULL PRIMARY KEY,
                TimeZone TEXT NOT NULL
            );
            CREATE TABLE SourceSnapshots (
                RunDate TEXT NOT NULL,
                SourceOrder INTEGER NOT NULL,
                Source TEXT NOT NULL,
                SourceUrl TEXT NOT NULL,
                RetrievedAt TEXT NOT NULL,
                EffectiveAsOf TEXT,
                PRIMARY KEY (RunDate, SourceOrder),
                UNIQUE (RunDate, SourceUrl),
                FOREIGN KEY (RunDate) REFERENCES DailyRuns(RunDate)
            );
            CREATE TABLE MortgageRates (
                RunDate TEXT NOT NULL,
                SourceOrder INTEGER NOT NULL,
                RateOrder INTEGER NOT NULL,
                Product TEXT NOT NULL,
                InterestRate TEXT NOT NULL,
                DiscountPoints TEXT,
                Apr TEXT NOT NULL,
                PRIMARY KEY (RunDate, SourceOrder, Product),
                UNIQUE (RunDate, SourceOrder, RateOrder),
                FOREIGN KEY (RunDate, SourceOrder) REFERENCES SourceSnapshots(RunDate, SourceOrder)
            );
            PRAGMA user_version = 1;
            """);
        await UpgradeReportingAsync(connection, transaction);
        foreach (var record in legacyRecords)
            await InsertAsync(connection, transaction, record);
        transaction.Commit();
        return legacyRecords.Count;
    }

    public async Task<bool> ContainsDateAsync(DateOnly date)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM DailyRuns WHERE RunDate = $date);";
        command.Parameters.AddWithValue("$date", DateText(date));
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    public async Task AddAsync(DailyRateRecord record)
    {
        ValidateRecord(record);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        await InsertAsync(connection, transaction, record);
        transaction.Commit();
    }

    public async Task<List<DailyRateRecord>> ReadAsync()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.RunDate, d.TimeZone, s.SourceOrder, s.Source, s.SourceUrl,
                   s.RetrievedAt, s.EffectiveAsOf, r.Product, r.InterestRate, r.DiscountPoints, r.Apr, d.IsTestData
            FROM DailyRuns d
            JOIN SourceSnapshots s ON s.RunDate = d.RunDate
            JOIN MortgageRates r ON r.RunDate = s.RunDate AND r.SourceOrder = s.SourceOrder
            ORDER BY d.RunDate, s.SourceOrder, r.RateOrder;
            """;
        using var reader = await command.ExecuteReaderAsync();
        var records = new List<DailyRateRecord>();
        DateOnly? currentDate = null;
        var currentSource = -1;
        List<SourceRateSnapshot> sources = [];
        List<MortgageRate> rates = [];
        while (await reader.ReadAsync())
        {
            var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (date != currentDate)
            {
                sources = [];
                records.Add(new DailyRateRecord(date, reader.GetString(1), sources, reader.GetBoolean(11)));
                currentDate = date;
                currentSource = -1;
            }
            if (reader.GetInt32(2) != currentSource)
            {
                rates = [];
                sources.Add(new SourceRateSnapshot(reader.GetString(3), reader.GetString(4),
                    reader.GetDateTimeOffset(5), new MortgageRateSnapshot(rates,
                        reader.IsDBNull(6) ? null : reader.GetString(6))));
                currentSource = reader.GetInt32(2);
            }
            rates.Add(new MortgageRate(reader.GetString(7), reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9), reader.GetDecimal(10)));
        }
        return records;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, DailyRateRecord record)
    {
        var date = DateText(record.Date);
        await ExecuteAsync(connection, transaction,
            "INSERT INTO DailyRuns (RunDate, TimeZone, IsTestData) VALUES ($date, $zone, $test);",
            ("$date", date), ("$zone", record.TimeZone), ("$test", record.IsTestData));
        for (var s = 0; s < record.Sources.Count; s++)
        {
            var source = record.Sources[s];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO SourceSnapshots (RunDate, SourceOrder, Source, SourceUrl, RetrievedAt, EffectiveAsOf)
                VALUES ($date, $order, $source, $url, $retrieved, $effective);
                """, ("$date", date), ("$order", s), ("$source", source.Source), ("$url", source.SourceUrl),
                ("$retrieved", source.RetrievedAt), ("$effective", source.Snapshot.EffectiveAsOf));
            for (var r = 0; r < source.Snapshot.Rates.Count; r++)
            {
                var rate = source.Snapshot.Rates[r];
                // Decimal parameters are stored as TEXT to preserve exact decimal values.
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO MortgageRates (RunDate, SourceOrder, RateOrder, Product, InterestRate, DiscountPoints, Apr)
                    VALUES ($date, $source, $order, $product, $interest, $points, $apr);
                    """, ("$date", date), ("$source", s), ("$order", r), ("$product", rate.Product),
                    ("$interest", rate.InterestRate), ("$points", rate.DiscountPoints), ("$apr", rate.Apr));
            }
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void ValidateRecord(DailyRateRecord record)
    {
        if (record is null || record.Date == default || string.IsNullOrWhiteSpace(record.TimeZone) ||
            record.Sources is null || record.Sources.Count != 2 ||
            record.Sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.Source) ||
                string.IsNullOrWhiteSpace(source.SourceUrl) || source.RetrievedAt == default ||
                source.Snapshot?.Rates is null || source.Snapshot.Rates.Count == 0 ||
                source.Snapshot.Rates.Any(rate => rate is null || string.IsNullOrWhiteSpace(rate.Product) ||
                    rate.InterestRate is < 0 or > 100 || rate.Apr is < 0 or > 100 || rate.DiscountPoints is < 0 or > 100)))
            throw new InvalidDataException("The rate history contains an invalid daily record.");
    }
}
