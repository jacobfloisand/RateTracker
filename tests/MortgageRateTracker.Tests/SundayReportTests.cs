using MortgageRateTracker.Reporting;

namespace MortgageRateTracker.Tests;

public sealed class SundayReportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MortgageReportTests", Guid.NewGuid().ToString("N"));
    private readonly DateOnly sunday = new(2026, 9, 27);
    private readonly FakeSender sender = new();
    private readonly FakeRenderer renderer = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private DailyRateHistory History => new(Path.Combine(directory, "rates.db"));
    private SundayReportService Service(bool retry = false) => new(History, renderer, sender, TimeProvider.System, output, error, retry);

    private async Task Initialize()
    {
        using var storageLock = History.AcquireLock();
        await History.InitializeAsync();
        await History.AddAsync(WeeklyReportTests.Day(sunday));
    }

    [Fact]
    public async Task SendsOnSundayAndDoesNotSendTwice()
    {
        await Initialize();
        Assert.Equal(0, await Service().RunAsync(sunday));
        Assert.Equal("Sent", (await History.GetEmailDeliveryAsync(sunday, GmailSettings.Recipient))!.Status);
        var before = await File.ReadAllBytesAsync(History.FilePath);
        Assert.Equal(0, await Service().RunAsync(sunday));
        Assert.Equal(1, sender.Calls);
        Assert.Equal(1, renderer.Calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(History.FilePath));
    }

    [Fact]
    public async Task WeekdayDoesNotRenderOrSend()
    {
        // Does not even need a database on weekdays.
        Assert.Equal(0, await Service().RunAsync(sunday.AddDays(1)));
        Assert.Equal(0, sender.Calls);
        Assert.Equal(0, renderer.Calls);
    }

    [Fact]
    public async Task FailedAuthenticationCanBeRetried()
    {
        await Initialize();
        sender.Failure = new EmailDeliveryException("Test authentication failure", false, new IOException());
        Assert.Equal(1, await Service().RunAsync(sunday));
        Assert.Equal("Failed", (await History.GetEmailDeliveryAsync(sunday, GmailSettings.Recipient))!.Status);
        sender.Failure = null;
        Assert.Equal(0, await Service().RunAsync(sunday));
        Assert.Equal(2, sender.Calls);
        Assert.Single(await History.ReadAsync());
    }

    [Fact]
    public async Task UncertainDeliveryRequiresExplicitRetry()
    {
        await Initialize();
        sender.Failure = new EmailDeliveryException("Test lost connection", true, new IOException());
        Assert.Equal(1, await Service().RunAsync(sunday));
        sender.Failure = null;
        Assert.Equal(1, await Service().RunAsync(sunday));
        Assert.Equal(1, sender.Calls);
        Assert.Equal("Unknown", (await History.GetEmailDeliveryAsync(sunday, GmailSettings.Recipient))!.Status);
        Assert.Equal(0, await Service(retry: true).RunAsync(sunday));
        Assert.Equal(2, sender.Calls);
    }

    [Fact]
    public async Task InterruptedAttemptIsNotAutomaticallyResent()
    {
        await Initialize();
        await History.SetEmailDeliveryAsync(sunday, GmailSettings.Recipient, "Sending", "test@example.com", DateTimeOffset.UtcNow, false);
        Assert.Equal(1, await Service().RunAsync(sunday));
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task MissingCredentialsDoNotRenderOrMarkEmailSent()
    {
        await Initialize();
        sender.Configured = false;
        Assert.Equal(1, await Service().RunAsync(sunday));
        Assert.Null(await History.GetEmailDeliveryAsync(sunday, GmailSettings.Recipient));
        Assert.Equal(0, renderer.Calls);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task RendererFailureDoesNotMarkEmailSentAndCanBeRetried()
    {
        await Initialize();
        renderer.Fail = true;
        Assert.Equal(1, await Service().RunAsync(sunday));
        Assert.Null(await History.GetEmailDeliveryAsync(sunday, GmailSettings.Recipient));
        renderer.Fail = false;
        Assert.Equal(0, await Service().RunAsync(sunday));
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task ExistingDailyRecordStillRunsSundayEmailWithoutRefetching()
    {
        await Initialize();
        var clock = new SundayClock();
        var service = Service();
        RateSource[] sources = [new("Never fetched", "https://example.com", "APR", () => throw new Exception("Must not fetch"))];
        Assert.Equal(0, await new DailyRateTracker(History, sources, clock, output, error, service.RunAsync).RunAsync());
        Assert.Equal(1, sender.Calls);
        Assert.Contains("already run", output.ToString());
    }

    [Fact]
    public async Task NewDailyRunSavesBeforeSundayReportAndRetriesEmailWithoutRefetching()
    {
        var fetches = 0;
        var sample = WeeklyReportTests.Day(sunday);
        var sources = sample.Sources.Select(source => new RateSource(source.Source, source.SourceUrl, "APR", () =>
        {
            fetches++;
            return Task.FromResult(source.Snapshot);
        })).ToArray();
        sender.Failure = new EmailDeliveryException("Test auth failure", false, new IOException());
        Assert.Equal(1, await new DailyRateTracker(History, sources, new SundayClock(), output, error, Service().RunAsync).RunAsync());
        Assert.Single(await History.ReadAsync());
        sender.Failure = null;
        Assert.Equal(0, await new DailyRateTracker(History, sources, new SundayClock(), output, error, Service().RunAsync).RunAsync());
        Assert.Equal(2, fetches);
        Assert.Equal(2, sender.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
    private sealed class SundayClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 27, 14, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private sealed class FakeRenderer : IReportRenderer
    {
        public int Calls;
        public bool Fail;
        public Task<ReportArtifacts> RenderAsync(WeeklyReport report)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("Test renderer failure");
            return Task.FromResult(new ReportArtifacts("report.html", "chart.png", "report.json", "email.eml"));
        }
    }
    private sealed class FakeSender : IReportSender
    {
        public int Calls;
        public bool Configured = true;
        public Exception? Failure;
        public void ValidateConfiguration()
        {
            if (!Configured) throw new InvalidOperationException("Test missing password");
        }
        public Task SendAsync(WeeklyReport report, ReportArtifacts artifacts, string messageId)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Task.CompletedTask;
        }
    }
}
