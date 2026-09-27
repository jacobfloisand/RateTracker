using MortgageRateTracker;
using MortgageRateTracker.Reporting;

try
{
    var options = AppOptions.Parse(args);
    if (options.Mode == AppMode.Help)
    {
        Console.WriteLine(AppOptions.Usage);
        return 0;
    }
    if (options.Mode == AppMode.InstallBrowser)
        return Microsoft.Playwright.Program.Main(["install", "chromium"]);

    var clock = TimeProvider.System;
    var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
    var history = new DailyRateHistory(options.Database);
    var renderer = new ReportRenderer(options.ReportDirectory ?? Path.Combine(Path.GetDirectoryName(history.FilePath)!, "reports"));
    if (options.Mode != AppMode.Run)
    {
        using var storageLock = history.AcquireLock();
        await history.InitializeAsync();
        var reportDate = options.ReportDate ?? today;
        if (reportDate > today)
            throw new ArgumentException("The report date cannot be in the future.");
        if (options.Mode == AppMode.Seed)
        {
            var count = await history.SeedTestDataAsync(reportDate, clock.LocalTimeZone.Id);
            Console.WriteLine($"Added {count} synthetic days to {history.FilePath}. Existing records and today's rates were preserved. Use --clear-test-data before deployment.");
            return 0;
        }
        if (options.Mode == AppMode.Clear)
        {
            Console.WriteLine($"Removed {await history.ClearTestDataAsync()} synthetic days. Real observations and email delivery history were preserved.");
            return 0;
        }
        var report = WeeklyReportBuilder.Build(await history.ReadAsync(), reportDate);
        var artifacts = await renderer.RenderAsync(report);
        using var email = EmailComposer.Create(report, artifacts, GmailSettings.Recipient);
        await email.WriteToAsync(artifacts.EmailPath);
        Console.WriteLine($"Preview: {artifacts.HtmlPath}\nChart: {artifacts.ChartPath}\nEmail preview: {artifacts.EmailPath}\nNo rates were fetched and no email was sent.");
        return 0;
    }

    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MortgageRateTracker/1.0");
    RateSource[] sources =
    [
        new("America First Credit Union - Current Rates", AmericaFirstRateSource.SourceUrl,
            "APR (as low as)", () => new AmericaFirstRateSource(httpClient).FetchAsync()),
        new("City Creek Mortgage - Utah Rate Summary", CityCreekRateSource.SourceUrl,
            "APR", () => new CityCreekRateSource(httpClient).FetchAsync())
    ];
    var sundayReports = new SundayReportService(history, renderer, new GmailReportSender(GmailSettings.FromEnvironment),
        clock, Console.Out, Console.Error, options.RetryEmail);
    return await new DailyRateTracker(history, sources, clock, Console.Out, Console.Error, sundayReports.RunAsync).RunAsync();
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException
    or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException
    or Microsoft.Playwright.PlaywrightException or InvalidDataException)
{
    Console.Error.WriteLine(exception.Message);
    if (exception is Microsoft.Playwright.PlaywrightException)
        Console.Error.WriteLine("Install the report browser with: dotnet run -- --install-browser");
    return 1;
}
