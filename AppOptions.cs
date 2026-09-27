using System.Globalization;

namespace MortgageRateTracker;

public enum AppMode { Run, Preview, Seed, Clear, InstallBrowser, Help }
public sealed record AppOptions(AppMode Mode, string Database, string? ReportDirectory, DateOnly? ReportDate, bool RetryEmail)
{
    public const string Usage = """
        Usage: MortgageRateTracker [--database <path>] [--report-dir <path>] [command]
          (no command)          Fetch once daily; send the report on Sunday.
          --preview-report      Create HTML, PNG, JSON, and email previews without fetching or sending.
          --seed-test-data       Add labeled synthetic history for missing days in the past year.
          --clear-test-data      Remove only labeled synthetic rate history.
          --install-browser     Install Playwright Chromium for report rendering.
          --report-date <date>   yyyy-MM-dd; allowed with --preview-report or --seed-test-data.
          --retry-email         Explicitly retry an uncertain Sunday email after checking Gmail Sent mail.
          --help                Show this help.
        """;

    public static AppOptions Parse(string[] args)
    {
        var mode = AppMode.Run;
        var database = Path.Combine("data", "mortgage-rates.db");
        string? reportDirectory = null;
        DateOnly? reportDate = null;
        var retryEmail = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length && !string.IsNullOrWhiteSpace(args[i])
                ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}.");
            var nextMode = args[i] switch
            {
                "--preview-report" => AppMode.Preview, "--seed-test-data" => AppMode.Seed,
                "--clear-test-data" => AppMode.Clear, "--install-browser" => AppMode.InstallBrowser,
                "--help" => AppMode.Help, _ => AppMode.Run
            };
            if (nextMode != AppMode.Run)
            {
                if (mode != AppMode.Run) throw new ArgumentException("Choose only one command.");
                mode = nextMode;
                continue;
            }
            switch (args[i])
            {
                case "--database": database = Value(); break;
                case "--report-dir": reportDirectory = Value(); break;
                case "--report-date":
                    if (!DateOnly.TryParseExact(Value(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        throw new ArgumentException("Use yyyy-MM-dd for --report-date.");
                    reportDate = date;
                    break;
                case "--retry-email": retryEmail = true; break;
                default: throw new ArgumentException($"Unknown option: {args[i]}.");
            }
        }
        if (reportDate.HasValue && mode is not (AppMode.Preview or AppMode.Seed))
            throw new ArgumentException("--report-date is only available for previewing or seeding test data.");
        if (retryEmail && mode != AppMode.Run) throw new ArgumentException("--retry-email is only available for normal runs.");
        if (Path.GetExtension(database).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a .db path for SQLite. Adjacent legacy JSON is imported automatically.");
        return new AppOptions(mode, Path.GetFullPath(database), reportDirectory, reportDate, retryEmail);
    }
}
