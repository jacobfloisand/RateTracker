using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace MortgageRateTracker.Reporting;

public sealed record GmailSettings(string Username, string AppPassword)
{
    public const string Recipient = "jacobfloisand@gmail.com";

    public static GmailSettings FromEnvironment()
    {
        var username = Environment.GetEnvironmentVariable("MORTGAGE_SMTP_USERNAME") ?? Recipient;
        var password = Environment.GetEnvironmentVariable("MORTGAGE_SMTP_APP_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Set MORTGAGE_SMTP_APP_PASSWORD to a Gmail app password to enable Sunday email. Use --preview-report to test without sending.");
        if (!MailboxAddress.TryParse(username, out var address) || address.Address != username)
            throw new InvalidOperationException("MORTGAGE_SMTP_USERNAME must be the sending Gmail address.");
        return new GmailSettings(username, string.Concat(password.Where(character => !char.IsWhiteSpace(character))));
    }
}

public interface IReportSender
{
    void ValidateConfiguration();
    Task SendAsync(WeeklyReport report, ReportArtifacts artifacts, string messageId);
}

public sealed class EmailDeliveryException(string message, bool deliveryUncertain, Exception innerException)
    : Exception(message, innerException)
{
    public bool DeliveryUncertain { get; } = deliveryUncertain;
}

public sealed class GmailReportSender(Func<GmailSettings> getSettings) : IReportSender
{
    public void ValidateConfiguration() => _ = getSettings();

    public async Task SendAsync(WeeklyReport report, ReportArtifacts artifacts, string messageId)
    {
        var settings = getSettings();
        using var message = EmailComposer.Create(report, artifacts, settings.Username, messageId);
        using var client = new SmtpClient { Timeout = 30_000 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var sending = false;
        try
        {
            await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls, timeout.Token);
            await client.AuthenticateAsync(settings.Username, settings.AppPassword, timeout.Token);
            sending = true;
            await client.SendAsync(message, timeout.Token);
        }
        catch (Exception exception) when (exception is MailKit.CommandException or MailKit.ProtocolException
            or IOException or System.Net.Sockets.SocketException or OperationCanceledException
            or System.Security.Authentication.AuthenticationException or MailKit.Security.AuthenticationException)
        {
            // After starting SMTP DATA, a dropped connection can mean either accepted
            // or rejected. Do not blindly resend a message Gmail may have accepted.
            throw new EmailDeliveryException(sending
                ? "Gmail delivery could not be confirmed. Check Sent mail before using --retry-email."
                : "Gmail connection or authentication failed. Check the SMTP environment settings and retry.", sending, exception);
        }
        // SMTP accepted the message. A disconnect failure must not turn it into a retry.
        try { await client.DisconnectAsync(true, timeout.Token); }
        catch (Exception exception) when (exception is IOException or MailKit.ProtocolException or MailKit.CommandException or OperationCanceledException) { }
    }
}

public static class EmailComposer
{
    public static MimeMessage Create(WeeklyReport report, ReportArtifacts artifacts, string sender, string? messageId = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Mortgage Rate Tracker", sender));
        message.To.Add(MailboxAddress.Parse(GmailSettings.Recipient));
        message.Subject = $"{(report.ContainsTestData ? "[TEST DATA] " : "")}Weekly mortgage rates — {report.AsOf:MMM d, yyyy}";
        message.MessageId = messageId ?? MimeUtils.GenerateMessageId();
        var body = new BodyBuilder { TextBody = ReportContent.PlainText(report) };
        var chart = body.LinkedResources.Add(artifacts.ChartPath, new ContentType("image", "png"));
        chart.ContentId = MimeUtils.GenerateMessageId();
        body.HtmlBody = $"""
            <!doctype html><html><body style="font-family:Arial,Helvetica,sans-serif;color:#172b46;margin:0;padding:24px;background:#f1f5f9">
            <div style="max-width:1150px;margin:auto;background:white;padding:24px">
            <h1 style="font-size:26px">Your weekly mortgage rate report</h1>
            <p>As of {report.AsOf:MMMM d, yyyy} · America First &amp; City Creek Mortgage</p>
            {ReportContent.TestBadge(report)}
            <img src="cid:{chart.ContentId}" alt="Weekly average mortgage interest rates by lender and loan term, plus America First rate-plus-points lines. The statistics table below provides the latest values."
                width="1100" style="display:block;width:100%;max-width:1100px;height:auto;margin:20px 0">
            <h2 style="font-size:20px">Rate statistics</h2>
            {ReportContent.StatisticsTable(report)}
            <p style="font-size:12px;line-height:1.6;color:#52657b">{ReportContent.Methodology(report)}</p>
            <p style="font-size:12px"><a href="{AmericaFirstRateSource.SourceUrl}">America First Current Rates</a> ·
            <a href="{CityCreekRateSource.SourceUrl}">City Creek Utah summary</a></p>
            </div></body></html>
            """;
        message.Body = body.ToMessageBody();
        return message;
    }
}
