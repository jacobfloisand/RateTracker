# Mortgage Rate Tracker

A .NET 10 console application that records mortgage rates daily and emails a
weekly D3 chart and rate statistics to **jacobfloisand@gmail.com** on Sundays.

## Setup and run

Install the .NET 10 SDK, then:

```sh
dotnet build
dotnet run -- --install-browser
dotnet run
```

The browser installation is a one-time setup per machine/user for rendering D3
charts as PNG images. Run it again after updating Playwright. Linux hosts may
also need [Playwright browser dependencies](https://playwright.dev/dotnet/docs/browsers#install-system-dependencies).
Run `dotnet run -- --help` for all options.

The app exits when finished; schedule it daily using cron, Task Scheduler, or
another scheduler. It uses the machine's local date and time zone. Make sure the
scheduled process has the same database path, browser installation, and SMTP
environment variables as your interactive session.

## Gmail email setup

1. Enable Google 2-Step Verification and create a dedicated
   [Gmail app password](https://support.google.com/accounts/answer/185833?hl=en).
2. Set these environment variables in the environment that runs the app:

| Variable | Meaning |
| --- | --- |
| `MORTGAGE_SMTP_USERNAME` | Sending Gmail address; defaults to `jacobfloisand@gmail.com` |
| `MORTGAGE_SMTP_APP_PASSWORD` | Gmail app password; required to send |

For a temporary Bash session, enter the password without putting it into shell history:

```sh
export MORTGAGE_SMTP_USERNAME='jacobfloisand@gmail.com'
read -rsp 'Gmail app password: ' MORTGAGE_SMTP_APP_PASSWORD
export MORTGAGE_SMTP_APP_PASSWORD
printf '\n'
dotnet run
```

Use the app password, not the normal Google account password. Credentials are
read from the environment and are not stored in SQLite, reports, or configuration
files. A `.env` file is not automatically loaded. Delivery uses `smtp.gmail.com`,
port 587, with required STARTTLS and certificate validation. No SMTP credentials
are needed for local previews.

### Sunday behavior

- Each run first checks whether today's rates are already stored. It fetches
  only if needed.
- On Sunday, after a successful daily save or skip, the app checks `WeeklyEmails`
  and sends the report if it has not already been sent that day.
- Successful SMTP acceptance is recorded to avoid repeat Sunday emails. A repeat
  run skips both fetching and sending when both are complete.
- A missing password, rendering failure, or connection/authentication failure
  returns exit code 1. Rates already saved remain intact; rerunning on Sunday
  retries email without fetching again.
- If a connection fails during sending, or the process stops after starting a
  send, acceptance may be uncertain. Automatic resending is stopped. Check Gmail
  Sent mail before using `dotnet run -- --retry-email`. SMTP cannot guarantee
  exactly-once delivery across a crash; this avoids blindly sending duplicates.
- Weekday runs do not send reports or require SMTP credentials. There is no
  automatic catch-up email if the app never runs on Sunday.

## Report and statistics

The chart has one line for each of the six lender/product combinations and shows
**weekly average interest rates** over the preceding calendar year, through the
report date. Each America First product also has a dotted **rate + points** line
in the same color: add that day's discount points to its interest rate, then
average those daily sums for the week (for example, 6.375% + 0.500% = 6.875%).
Days with missing points are excluded from the additional line. The existing
statistics table continues to describe the original interest rates and APRs.

Weeks run Monday–Sunday. Each saved daily observation has equal
weight; missing days are not invented, and entirely missing weeks appear as gaps.
The first and current week may be partial. Weekly observation counts are shown.

For each product, the report includes:

- Latest interest rate, APR, discount points, and observation date.
- This week's average and its change from the prior week's average in basis points.
- Yearly low, high, and average of daily interest rates, plus observation count.

D3 7.9.0 is bundled locally with its license. Playwright renders the same D3 chart
as a PNG embedded in the email, since email clients do not execute D3 JavaScript.
The email also contains HTML and plain-text statistics. Local HTML previews are
self-contained and interactive: toggle products in the legend and hover over the
chart to inspect weekly values. The preview needs no CDN or external scripts.

```sh
dotnet run -- --preview-report
# Optional historical preview (no fetching or sending):
dotnet run -- --preview-report --report-date 2026-09-27
```

Files are written beside the database in `reports/YYYY-MM-DD/`:

- `report.html`: interactive D3 report and statistics.
- `chart.png`: chart used in the email.
- `report.json`: calculated report data.
- `email.eml`: full email preview, created by `--preview-report`.

Override the destination with `--report-dir /absolute/path/reports`. Preview mode
never sends mail or marks a Sunday report as sent.

## Test history

```sh
dotnet run -- --seed-test-data
dotnet run -- --preview-report
```

Seeding fills missing days in the past year through yesterday with deterministic,
synthetic rates for all six products. It never overwrites real records or seeds
today. Repeating the command does not duplicate records. `DailyRuns.IsTestData`
marks synthetic days, and reports/emails that include any of them have a prominent
**TEST DATA INCLUDED** label and a `[TEST DATA]` email subject prefix.

Before deployment:

```sh
dotnet run -- --clear-test-data
```

This removes only marked synthetic rates and their source/day records. Real
observations and email delivery history are preserved. Existing preview files
remain test artifacts; regenerate previews after cleanup. Do not use the old
sample reports as production history.

## Data sources

Each new daily run fetches public HTML with `HttpClient` and parses it using
HtmlAgilityPack:

- [America First](https://www.americafirst.com/loans/mortgage-loans/mortgage-loans.html#rates):
  **Current Rates** for 10, 15, 20, and 30 Year Fixed products, including interest
  rate, discount points, and APR (as low as). Government Rates are excluded.
- [City Creek Mortgage](https://citycreekmortgage.com/todays-rates/): the paragraph
  beginning “As of [date], the rates in Utah are…”, including rates and APRs for
  15- and 30-year fixed products. Discount points are not provided in this summary
  and are stored as SQL `NULL`.

Requests time out after 30 seconds. Both sources must succeed before a daily
record is saved. A failed fetch leaves history unchanged and can be retried.
Website layout or wording changes may require updating the parsers. Published
effective dates can precede the daily observation date.

## SQLite storage and scheduling

The default database is `data/mortgage-rates.db`, relative to the working directory.
SQLite is embedded and needs no separate server. Use a stable absolute path for
scheduled runs:

```sh
dotnet run --project /absolute/path/MortgageRateTracker.csproj -- --database /absolute/path/mortgage-rates.db
```

Tables:

- `DailyRuns`: successful local dates, time zones, and test-data markers.
- `SourceSnapshots`: lender names/URLs, UTC retrieval times, and source effective dates.
- `MortgageRates`: individual products, rates, points, and APRs.
- `WeeklyEmails`: recipient/date, message ID, delivery status, and test-data flag.

Daily saves and schema upgrades use transactions. Primary keys prevent duplicate
dates/products. An application `.lock` sidecar prevents overlapping runs from
fetching or sending simultaneously. Keep the database between runs/deployments.
The default `data/` directory, including report artifacts, is excluded from Git.

Rates use exact decimal text columns, following
[Microsoft.Data.Sqlite's decimal mapping](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/types).
C# restores them as `decimal`; cast to `REAL` for approximate SQL calculations.

Existing version-one databases are upgraded automatically while preserving their
rates. On first creation, adjacent legacy JSON with the same base name is imported
transactionally, retained as backup, and not read or updated again.

## Tests

```sh
dotnet test tests/MortgageRateTracker.Tests/MortgageRateTracker.Tests.csproj
```

Tests use temporary SQLite databases, synthetic HTML, and fake mail/rendering
services. They cover parsing, daily skips, transactions, schema migration,
weekly statistics, date boundaries, missing weeks, Sunday sending and retries,
MIME email contents, and test-data seeding/cleanup. They do not send real email.
To test Chromium and D3 together, run `--preview-report` after installing the
browser and inspect the generated report.
