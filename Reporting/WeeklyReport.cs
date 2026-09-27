namespace MortgageRateTracker.Reporting;

public sealed record WeeklyPoint(DateOnly WeekEnding, decimal? Average, int Observations);
public sealed record RateStatistics(DateOnly LatestDate, decimal LatestRate, decimal LatestApr,
    decimal? LatestDiscountPoints, decimal? WeeklyAverage, int WeeklyObservations,
    decimal? WeeklyChangeBasisPoints, decimal YearLow, decimal YearHigh, decimal YearAverage, int Observations);
public sealed record RateSeries(string Source, string SourceUrl, string Product,
    IReadOnlyList<WeeklyPoint> Weeks, RateStatistics Statistics,
    IReadOnlyList<WeeklyPoint>? RatePlusPointsWeeks = null);
public sealed record WeeklyReport(DateOnly AsOf, DateOnly From, bool ContainsTestData,
    int TestDays, IReadOnlyList<RateSeries> Series);

public static class WeeklyReportBuilder
{
    public static DateOnly WeekEnding(DateOnly date) => date.AddDays((7 - (int)date.DayOfWeek) % 7);

    public static WeeklyReport Build(IEnumerable<DailyRateRecord> history, DateOnly asOf)
    {
        var from = asOf.AddYears(-1).AddDays(1);
        var records = history.Where(record => record.Date >= from && record.Date <= asOf)
            .OrderBy(record => record.Date).ToArray();
        var weeks = new List<DateOnly>();
        for (var week = WeekEnding(from); week <= WeekEnding(asOf); week = week.AddDays(7))
            weeks.Add(week);
        var observations = records.SelectMany(record => record.Sources.SelectMany(source => source.Snapshot.Rates
            .Select(rate => new { record.Date, source.Source, source.SourceUrl, Rate = rate })));
        var series = new List<RateSeries>();
        foreach (var group in observations.GroupBy(item => (item.SourceUrl, item.Rate.Product))
                     .OrderBy(group => group.First().Source, StringComparer.Ordinal).ThenBy(group => group.Key.Product, StringComparer.Ordinal))
        {
            var items = group.OrderBy(item => item.Date).ToArray();
            var buckets = items.GroupBy(item => WeekEnding(item.Date)).ToDictionary(group => group.Key);
            var points = weeks.Select(week => buckets.TryGetValue(week, out var values)
                ? new WeeklyPoint(week, values.Average(item => item.Rate.InterestRate), values.Count())
                : new WeeklyPoint(week, null, 0)).ToArray();
            // Sum each day's paired values before averaging. Missing points are
            // excluded from this derived line rather than treated as zero.
            var ratePlusPoints = group.Key.SourceUrl == AmericaFirstRateSource.SourceUrl
                ? weeks.Select(week =>
                {
                    var values = buckets.TryGetValue(week, out var bucket)
                        ? bucket.Where(item => item.Rate.DiscountPoints.HasValue).ToArray() : [];
                    return new WeeklyPoint(week, values.Length == 0 ? null
                        : values.Average(item => item.Rate.InterestRate + item.Rate.DiscountPoints!.Value), values.Length);
                }).ToArray()
                : null;
            var current = points[^1];
            var previous = points.Length > 1 ? points[^2] : null;
            var latest = items[^1];
            series.Add(new RateSeries(latest.Source, latest.SourceUrl, latest.Rate.Product, points,
                new RateStatistics(latest.Date, latest.Rate.InterestRate, latest.Rate.Apr, latest.Rate.DiscountPoints,
                    current.Average, current.Observations,
                    current.Average.HasValue && previous?.Average is decimal prior
                        ? (current.Average.Value - prior) * 100 : null,
                    items.Min(item => item.Rate.InterestRate), items.Max(item => item.Rate.InterestRate),
                    items.Average(item => item.Rate.InterestRate), items.Length), ratePlusPoints));
        }
        return new WeeklyReport(asOf, from, records.Any(record => record.IsTestData),
            records.Count(record => record.IsTestData), series);
    }
}
