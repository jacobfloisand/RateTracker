using Microsoft.Data.Sqlite;

namespace MortgageRateTracker;

public sealed record EmailDelivery(string Status, string MessageId);

public sealed partial class DailyRateHistory
{
    private static Task UpgradeReportingAsync(SqliteConnection connection, SqliteTransaction transaction) =>
        ExecuteAsync(connection, transaction, """
            ALTER TABLE DailyRuns ADD COLUMN IsTestData INTEGER NOT NULL DEFAULT 0 CHECK (IsTestData IN (0, 1));
            CREATE TABLE WeeklyEmails (
                ReportDate TEXT NOT NULL,
                Recipient TEXT NOT NULL,
                Status TEXT NOT NULL CHECK (Status IN ('Sending', 'Sent', 'Failed', 'Unknown')),
                MessageId TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                ContainsTestData INTEGER NOT NULL CHECK (ContainsTestData IN (0, 1)),
                PRIMARY KEY (ReportDate, Recipient)
            );
            PRAGMA user_version = 2;
            """);

    public async Task<EmailDelivery?> GetEmailDeliveryAsync(DateOnly date, string recipient)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status, MessageId FROM WeeklyEmails WHERE ReportDate = $date AND Recipient = $recipient;";
        command.Parameters.AddWithValue("$date", DateText(date));
        command.Parameters.AddWithValue("$recipient", recipient);
        using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new EmailDelivery(reader.GetString(0), reader.GetString(1)) : null;
    }

    // The caller holds AcquireLock across checking delivery, sending, and recording the outcome.
    public async Task SetEmailDeliveryAsync(DateOnly date, string recipient, string status,
        string messageId, DateTimeOffset updatedAt, bool containsTestData)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO WeeklyEmails (ReportDate, Recipient, Status, MessageId, UpdatedAt, ContainsTestData)
            VALUES ($date, $recipient, $status, $message, $updated, $test)
            ON CONFLICT (ReportDate, Recipient) DO UPDATE SET
                Status = excluded.Status, MessageId = excluded.MessageId,
                UpdatedAt = excluded.UpdatedAt, ContainsTestData = excluded.ContainsTestData;
            """, ("$date", DateText(date)), ("$recipient", recipient), ("$status", status),
            ("$message", messageId), ("$updated", updatedAt), ("$test", containsTestData));
        transaction.Commit();
    }

    public async Task<int> SeedTestDataAsync(DateOnly today, string timeZone)
    {
        var existing = (await ReadAsync()).Select(record => record.Date).ToHashSet();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var start = today.AddYears(-1).AddDays(1);
        var inserted = 0;
        for (var date = start; date < today; date = date.AddDays(1))
        {
            if (existing.Contains(date))
                continue;
            var day = date.DayNumber - start.DayNumber;
            var week = day / 7;
            var movement = (decimal)(0.32 * Math.Sin(week / 5.0) + 0.12 * Math.Cos(week / 2.7) - 0.30 * day / 365.0);
            MortgageRate Rate(int years, decimal baseline, decimal? points, decimal aprOffset) =>
                new($"{years} Year Fixed", decimal.Round(baseline + movement, 3), points,
                    decimal.Round(baseline + movement + aprOffset, 3));
            var retrieved = new DateTimeOffset(date.ToDateTime(new TimeOnly(14, 0)), TimeSpan.Zero);
            await InsertAsync(connection, transaction, new DailyRateRecord(date, timeZone,
            [
                new("America First Credit Union - Current Rates", AmericaFirstRateSource.SourceUrl, retrieved,
                    new MortgageRateSnapshot([Rate(10, 6.65m, 0.5m, 0.331m), Rate(15, 6.775m, 0.375m, 0.216m),
                        Rate(20, 7.15m, 0.625m, 0.208m), Rate(30, 7.40m, 0.375m, 0.136m)], "Synthetic test data")),
                new("City Creek Mortgage - Utah Rate Summary", CityCreekRateSource.SourceUrl, retrieved,
                    new MortgageRateSnapshot([Rate(15, 6.65m, null, 0), Rate(30, 7.27m, null, 0.112m)], "Synthetic test data"))
            ], IsTestData: true));
            inserted++;
        }
        transaction.Commit();
        return inserted;
    }

    public async Task<int> ClearTestDataAsync()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM DailyRuns WHERE IsTestData = 1;";
        var removed = Convert.ToInt32(await count.ExecuteScalarAsync());
        await ExecuteAsync(connection, transaction, """
            DELETE FROM MortgageRates WHERE RunDate IN (SELECT RunDate FROM DailyRuns WHERE IsTestData = 1);
            DELETE FROM SourceSnapshots WHERE RunDate IN (SELECT RunDate FROM DailyRuns WHERE IsTestData = 1);
            DELETE FROM DailyRuns WHERE IsTestData = 1;
            """);
        transaction.Commit();
        return removed;
    }
}
