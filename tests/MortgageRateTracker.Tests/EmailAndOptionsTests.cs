using MimeKit;
using MortgageRateTracker.Reporting;

namespace MortgageRateTracker.Tests;

public sealed class EmailAndOptionsTests
{
    [Fact]
    public async Task EmailContainsInlineChartPlainTextStatsAndTestLabelWithoutJavascript()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MortgageMimeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var chartPath = Path.Combine(directory, "chart.png");
            await File.WriteAllBytesAsync(chartPath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
            var date = new DateOnly(2026, 9, 27);
            var report = WeeklyReportBuilder.Build([WeeklyReportTests.Day(date, test: true)], date);
            using var message = EmailComposer.Create(report, new ReportArtifacts("", chartPath, "", ""), GmailSettings.Recipient, "test@example.com");
            using var buffer = new MemoryStream();
            await message.WriteToAsync(buffer);
            buffer.Position = 0;
            using var loaded = await MimeMessage.LoadAsync(buffer);
            Assert.Equal(GmailSettings.Recipient, Assert.Single(loaded.To.Mailboxes).Address);
            Assert.StartsWith("[TEST DATA]", loaded.Subject);
            Assert.Contains("Year", loaded.HtmlBody);
            Assert.Contains("weekly avg", loaded.TextBody);
            Assert.DoesNotContain("<script", loaded.HtmlBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<svg", loaded.HtmlBody, StringComparison.OrdinalIgnoreCase);
            var chart = Assert.Single(loaded.BodyParts.OfType<MimePart>(), part => part.ContentType.MimeType == "image/png");
            Assert.Contains($"cid:{chart.ContentId}", loaded.HtmlBody);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PreviewOptionsAllowCustomDatabaseAndDate()
    {
        var options = AppOptions.Parse(["--database", "sample.db", "--preview-report", "--report-date", "2026-09-27"]);
        Assert.Equal(AppMode.Preview, options.Mode);
        Assert.Equal(new DateOnly(2026, 9, 27), options.ReportDate);
        Assert.EndsWith("sample.db", options.Database);
    }

    [Theory]
    [InlineData("--database")]
    [InlineData("--unknown")]
    public void RejectsInvalidOption(string option) => Assert.Throws<ArgumentException>(() => AppOptions.Parse([option]));

    [Fact]
    public void RejectsConflictingModes() => Assert.Throws<ArgumentException>(() => AppOptions.Parse(["--preview-report", "--clear-test-data"]));

    [Fact]
    public void DoesNotAllowFakingDateOfNormalDailyRun() => Assert.Throws<ArgumentException>(() => AppOptions.Parse(["--report-date", "2026-09-27"]));
}
