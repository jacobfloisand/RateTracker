using MimeKit.Utils;

namespace MortgageRateTracker.Reporting;

public sealed class SundayReportService(DailyRateHistory history, IReportRenderer renderer,
    IReportSender sender, TimeProvider clock, TextWriter output, TextWriter error, bool retryUncertainDelivery = false)
{
    // Called with the same application lock held by DailyRateTracker.
    public async Task<int> RunAsync(DateOnly today)
    {
        if (today.DayOfWeek != DayOfWeek.Sunday)
            return 0;
        try
        {
            var delivery = await history.GetEmailDeliveryAsync(today, GmailSettings.Recipient);
            if (delivery?.Status == "Sent")
            {
                await output.WriteLineAsync($"Sunday report for {today:yyyy-MM-dd} has already been sent to {GmailSettings.Recipient}.");
                return 0;
            }
            if (delivery?.Status is "Sending" or "Unknown" && !retryUncertainDelivery)
            {
                await error.WriteLineAsync("The previous email attempt has an uncertain outcome. Check Gmail Sent mail before explicitly retrying with --retry-email.");
                return 1;
            }
            sender.ValidateConfiguration();
            var report = WeeklyReportBuilder.Build(await history.ReadAsync(), today);
            if (report.Series.Count == 0)
                throw new InvalidOperationException("No saved rates are available for the Sunday report.");
            var artifacts = await renderer.RenderAsync(report);
            var messageId = delivery?.MessageId ?? MimeUtils.GenerateMessageId();
            await history.SetEmailDeliveryAsync(today, GmailSettings.Recipient, "Sending", messageId,
                clock.GetUtcNow(), report.ContainsTestData);
            try
            {
                await sender.SendAsync(report, artifacts, messageId);
            }
            catch (EmailDeliveryException exception)
            {
                await history.SetEmailDeliveryAsync(today, GmailSettings.Recipient,
                    exception.DeliveryUncertain ? "Unknown" : "Failed", messageId, clock.GetUtcNow(), report.ContainsTestData);
                throw;
            }
            await history.SetEmailDeliveryAsync(today, GmailSettings.Recipient, "Sent", messageId,
                clock.GetUtcNow(), report.ContainsTestData);
            await output.WriteLineAsync($"Sent Sunday report to {GmailSettings.Recipient}. Preview: {artifacts.HtmlPath}");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException or Microsoft.Playwright.PlaywrightException or EmailDeliveryException)
        {
            await error.WriteLineAsync($"Sunday report was not completed: {exception.Message}");
            await error.WriteLineAsync("Saved rates are preserved. A repeat run will skip rate fetching and retry a safely failed email attempt.");
            return 1;
        }
    }
}
