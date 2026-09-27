using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace MortgageRateTracker.Reporting;

public sealed record ReportArtifacts(string HtmlPath, string ChartPath, string JsonPath, string EmailPath);
public interface IReportRenderer
{
    Task<ReportArtifacts> RenderAsync(WeeklyReport report);
}

public sealed class ReportRenderer(string outputDirectory) : IReportRenderer
{
    public async Task<ReportArtifacts> RenderAsync(WeeklyReport report)
    {
        var directory = Path.GetFullPath(Path.Combine(outputDirectory, report.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(directory);
        var artifacts = new ReportArtifacts(Path.Combine(directory, "report.html"), Path.Combine(directory, "chart.png"),
            Path.Combine(directory, "report.json"), Path.Combine(directory, "email.eml"));
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
        });
        await File.WriteAllTextAsync(artifacts.JsonPath, json);
        var html = BuildHtml(report, json);
        await File.WriteAllTextAsync(artifacts.HtmlPath, html);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1280, Height = 1100 }, DeviceScaleFactor = 2
        });
        // The preview is fully self-contained. Rendering never needs external web requests.
        await context.RouteAsync("**/*", route => route.AbortAsync());
        var page = await context.NewPageAsync();
        await page.SetContentAsync(html);
        await page.WaitForFunctionAsync("window.__chartReady === true", null, new() { Timeout = 15_000 });
        await page.Locator("#chart-card").ScreenshotAsync(new() { Path = artifacts.ChartPath });
        return artifacts;
    }

    private static string Asset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MortgageRateTracker.Reporting.Assets.{name}")
            ?? throw new InvalidOperationException($"Missing embedded report asset: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string BuildHtml(WeeklyReport report, string json) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Mortgage Rate Tracker · {{report.AsOf:yyyy-MM-dd}}</title>
        <style>
        *{box-sizing:border-box}body{margin:0;background:#f1f5f9;color:#172b46;font-family:Arial,Helvetica,sans-serif}
        main{max-width:1220px;margin:40px auto;padding:0 24px 40px}.eyebrow{font-size:12px;letter-spacing:2px;text-transform:uppercase;color:#53708e;font-weight:700}
        h1{font-size:36px;letter-spacing:-1px;margin:12px 0}h2{font-size:21px;margin:0 0 8px}.subtitle,p{line-height:1.6;color:#52657b}
        .card{background:white;border:1px solid #dde5ef;border-radius:16px;padding:24px;margin-top:24px;box-shadow:0 3px 14px #172b4605}
        .tag{display:inline-block;background:#fff0cc;color:#7c4900;padding:8px 12px;border-radius:7px;font-size:13px;font-weight:700;margin:8px 0}
        .chart-head{display:flex;justify-content:space-between;gap:16px;align-items:start}.range{font-size:13px;color:#64748b;white-space:nowrap}
        #chart svg{display:block;width:100%;font-family:Arial,Helvetica,sans-serif}#legend{display:flex;flex-wrap:wrap;gap:10px 24px;margin-top:16px}
        #legend button{background:none;border:0;padding:5px 0;color:#334155;font-size:13px;cursor:pointer;display:flex;align-items:center;gap:8px}
        .swatch{width:22px;border-top:3px solid}.note{font-size:12px;color:#64748b;line-height:1.6;margin-bottom:0}
        #tooltip{display:none;background:#172b46;color:white;padding:12px 16px;border-radius:8px;font-size:12px;line-height:1.8;margin-top:12px}
        .table-wrap{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:12px}th{text-align:right;color:#64748b;font-size:11px;font-weight:600;padding:14px 10px;border-bottom:1px solid #dfe6ef}
        td{text-align:right;padding:14px 10px;border-bottom:1px solid #edf1f6;white-space:nowrap}th:first-child,td:first-child{text-align:left}td small{display:block;font-size:10px;color:#64748b;margin-top:5px}
        a{color:#185adb}.footer{font-size:12px;color:#64748b;margin:24px 0;line-height:1.7}
        @media(max-width:650px){main{padding:0 12px}.card{padding:16px}h1{font-size:28px}.chart-head{display:block} }
        </style></head><body><main>
        <div class="eyebrow">Mortgage Rate Tracker / Weekly report</div>
        <h1>A year of mortgage rates.</h1>
        <div class="subtitle">America First &amp; City Creek Mortgage · As of {{report.AsOf:MMMM d, yyyy}}</div>
        <section id="chart-card" class="card">
          <div class="chart-head"><div><h2>Weekly mortgage rate trends</h2><div class="note">Interest rates and America First rate + points</div></div>
          <div class="range">{{report.From:MMM d, yyyy}} – {{report.AsOf:MMM d, yyyy}}</div></div>
          {{ReportContent.TestBadge(report)}}
          <div id="legend" aria-label="Toggle loan products"></div><div id="chart"></div>
          <p class="note">Monday–Sunday weeks · Average of available daily observations · Missing weeks appear as gaps<br>Dotted lines: daily interest rate + discount points, averaged weekly</p>
        </section>
        <div id="tooltip" role="status"></div>
        <section class="card"><h2>Rates at a glance</h2><p class="note">Weekly change compares this week's average with the previous week. 1 basis point = 0.01 percentage point.</p>
        <div class="table-wrap">{{ReportContent.StatisticsTable(report)}}</div></section>
        <div class="footer">{{ReportContent.Methodology(report)}}<br>
        Sources: <a href="{{AmericaFirstRateSource.SourceUrl}}">America First Current Rates</a> · <a href="{{CityCreekRateSource.SourceUrl}}">City Creek Utah summary</a></div>
        </main><script id="report-data" type="application/json">{{json}}</script>
        <script>{{Asset("d3.min.js")}}</script><script>{{Asset("chart.js")}}</script></body></html>
        """;
}

public static class ReportContent
{
    public static string Percent(decimal? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) + (value.HasValue ? "%" : "—");
    public static string BasisPoints(decimal? value) => value?.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + (value.HasValue ? " bp" : "—");
    public static string Lender(RateSeries series) => series.SourceUrl == AmericaFirstRateSource.SourceUrl ? "America First"
        : series.SourceUrl == CityCreekRateSource.SourceUrl ? "City Creek" : series.Source;
    public static string TestBadge(WeeklyReport report) => report.ContainsTestData
        ? $"<div class=\"tag\" style=\"background:#fff0cc;color:#7c4900;padding:10px\">TEST DATA INCLUDED · {report.TestDays} synthetic days · For testing only</div>" : "";
    public static string Methodology(WeeklyReport report) =>
        $"Window: {report.From:yyyy-MM-dd} through {report.AsOf:yyyy-MM-dd}. Weeks end Sunday; the first and current weeks may be partial. " +
        "America First dotted lines average each day's interest rate plus discount points; days with missing points are excluded from those lines. " +
        "Averages weight each saved day equally. Missing days are not filled in. Latest values show their observation date; yearly low, high, and average use daily interest rates. " +
        "Published lender effective dates may precede observation dates. Rates have different points and assumptions; this report is a history of advertised rates.";

    public static string StatisticsTable(WeeklyReport report)
    {
        var html = new StringBuilder("<table style=\"width:100%;border-collapse:collapse;font-family:Arial,sans-serif;font-size:12px\"><thead><tr>");
        foreach (var heading in new[] { "Lender / product", "Latest rate", "Latest APR", "Points", "Week avg", "Week Δ", "Year low", "Year high", "Year avg", "Days" })
            html.Append($"<th style=\"padding:10px 6px;border-bottom:1px solid #ccd5e0;text-align:left\">{heading}</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var series in report.Series)
        {
            var s = series.Statistics;
            html.Append($"<tr><td style=\"padding:12px 6px;border-bottom:1px solid #e2e8f0\"><strong>{WebUtility.HtmlEncode(Lender(series))}</strong><br>{WebUtility.HtmlEncode(series.Product)}</td>");
            var cells = new[]
            {
                $"{Percent(s.LatestRate)}<small>as of {s.LatestDate:yyyy-MM-dd}</small>", Percent(s.LatestApr),
                Percent(s.LatestDiscountPoints), $"{Percent(s.WeeklyAverage)}<small>{s.WeeklyObservations} day(s)</small>",
                BasisPoints(s.WeeklyChangeBasisPoints), Percent(s.YearLow), Percent(s.YearHigh), Percent(s.YearAverage), s.Observations.ToString(CultureInfo.InvariantCulture)
            };
            foreach (var cell in cells)
                html.Append($"<td style=\"padding:12px 6px;border-bottom:1px solid #e2e8f0\">{cell.Replace("<small>", "<br><small style=\"color:#64748b\">")}</td>");
            html.Append("</tr>");
        }
        return html.Append("</tbody></table>").ToString();
    }

    public static string PlainText(WeeklyReport report)
    {
        var text = new StringBuilder($"Mortgage rates — {report.AsOf:yyyy-MM-dd}\n");
        if (report.ContainsTestData) text.AppendLine($"TEST DATA INCLUDED: {report.TestDays} synthetic days. For testing only.");
        foreach (var series in report.Series)
        {
            var s = series.Statistics;
            text.AppendLine($"{Lender(series)} {series.Product}: latest {Percent(s.LatestRate)} (APR {Percent(s.LatestApr)}, points {Percent(s.LatestDiscountPoints)}) as of {s.LatestDate:yyyy-MM-dd}; " +
                $"weekly avg {Percent(s.WeeklyAverage)} ({s.WeeklyObservations} days), change {BasisPoints(s.WeeklyChangeBasisPoints)}; " +
                $"year low/high/avg {Percent(s.YearLow)} / {Percent(s.YearHigh)} / {Percent(s.YearAverage)} ({s.Observations} observations).");
        }
        return text.AppendLine(Methodology(report)).ToString();
    }
}
