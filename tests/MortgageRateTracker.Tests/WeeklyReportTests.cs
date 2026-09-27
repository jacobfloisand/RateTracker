using MortgageRateTracker.Reporting;

namespace MortgageRateTracker.Tests;

public class WeeklyReportTests
{
    private static readonly DateOnly Sunday = new(2026, 9, 27);

    internal static DailyRateRecord Day(DateOnly date, decimal rate = 6m, bool test = false) =>
        new(date, "America/Denver",
        [
            new("America First", AmericaFirstRateSource.SourceUrl, new DateTimeOffset(date.ToDateTime(new TimeOnly(14, 0)), TimeSpan.Zero),
                new MortgageRateSnapshot([new MortgageRate("15 Year Fixed", rate, .5m, rate + .2m)], "Published date")),
            new("City Creek", CityCreekRateSource.SourceUrl, new DateTimeOffset(date.ToDateTime(new TimeOnly(14, 0)), TimeSpan.Zero),
                new MortgageRateSnapshot([new MortgageRate("15 Year Fixed", rate + .5m, null, rate + .7m)], "Published date"))
        ], test);

    [Fact]
    public void CalculatesDailyWeightedWeeklyAndYearStatisticsWithoutCombiningLenders()
    {
        var report = WeeklyReportBuilder.Build([Day(Sunday.AddDays(-7), 5m), Day(Sunday.AddDays(-6), 6m), Day(Sunday, 8m)], Sunday);
        Assert.Equal(2, report.Series.Count);
        var stats = report.Series[0].Statistics;
        Assert.Equal(7m, stats.WeeklyAverage);
        Assert.Equal(2, stats.WeeklyObservations);
        Assert.Equal(200m, stats.WeeklyChangeBasisPoints);
        Assert.Equal(5m, stats.YearLow);
        Assert.Equal(8m, stats.YearHigh);
        Assert.Equal(19m / 3, stats.YearAverage);
        Assert.Equal(8m, stats.LatestRate);
        Assert.Equal(8.2m, stats.LatestApr);
        Assert.Equal(Sunday, stats.LatestDate);
        Assert.Equal(7.5m, report.Series[1].Statistics.WeeklyAverage);
        Assert.Null(report.Series[1].Statistics.LatestDiscountPoints);
    }

    [Theory]
    [InlineData(2026, 9, 21)]
    [InlineData(2026, 9, 26)]
    [InlineData(2026, 9, 27)]
    public void GroupsMondayThroughSunday(int year, int month, int day) =>
        Assert.Equal(Sunday, WeeklyReportBuilder.WeekEnding(new DateOnly(year, month, day)));

    [Fact]
    public void MissingWeeksRemainGapsAndAreNotUsedAsPreviousWeek()
    {
        var report = WeeklyReportBuilder.Build([Day(Sunday.AddDays(-14), 5m), Day(Sunday, 7m)], Sunday);
        var series = report.Series[0];
        Assert.Null(series.Weeks[^2].Average);
        Assert.Equal(0, series.Weeks[^2].Observations);
        Assert.Null(series.Statistics.WeeklyChangeBasisPoints);
    }

    [Fact]
    public void LimitsWindowToPastCalendarYearAndMarksOnlyIncludedSyntheticData()
    {
        var from = Sunday.AddYears(-1).AddDays(1);
        var report = WeeklyReportBuilder.Build([Day(from.AddDays(-1), 1m, true), Day(from, 6m), Day(Sunday, 7m), Day(Sunday.AddDays(1), 99m, true)], Sunday);
        Assert.Equal(from, report.From);
        Assert.False(report.ContainsTestData);
        Assert.Equal(2, report.Series[0].Statistics.Observations);
        Assert.Equal(6m, report.Series[0].Statistics.YearLow);
        Assert.Equal(7m, report.Series[0].Statistics.YearHigh);
    }

    [Fact]
    public void LabelsSyntheticDaysAndKeepsStaleLatestObservationDate()
    {
        var report = WeeklyReportBuilder.Build([Day(Sunday.AddDays(-8), 6m, true)], Sunday);
        Assert.True(report.ContainsTestData);
        Assert.Equal(1, report.TestDays);
        Assert.Null(report.Series[0].Statistics.WeeklyAverage);
        Assert.Equal(Sunday.AddDays(-8), report.Series[0].Statistics.LatestDate);
        Assert.Contains("TEST DATA", ReportContent.PlainText(report));
    }

    private static DailyRateRecord WithPoints(DailyRateRecord record, decimal? points)
    {
        var source = record.Sources[0];
        return record with { Sources = [source with
        {
            Snapshot = source.Snapshot with { Rates = [source.Snapshot.Rates[0] with { DiscountPoints = points }] }
        }, record.Sources[1]] };
    }

    [Fact]
    public void AddsAmericaFirstDailyRateAndPointsBeforeWeeklyAveraging()
    {
        var report = WeeklyReportBuilder.Build([
            WithPoints(Day(Sunday.AddDays(-1), 6m), .5m),
            WithPoints(Day(Sunday, 7m), 1m)
        ], Sunday);
        var series = report.Series[0];
        Assert.Equal(6.5m, series.Weeks[^1].Average);
        Assert.Equal(7.25m, series.RatePlusPointsWeeks![^1].Average);
        Assert.Equal(2, series.RatePlusPointsWeeks[^1].Observations);
        Assert.Equal(7m, series.Statistics.LatestRate);
        Assert.Null(report.Series[1].RatePlusPointsWeeks);
    }

    [Fact]
    public void RatePlusPointsExcludesMissingPointsAndKeepsZeroPoints()
    {
        var report = WeeklyReportBuilder.Build([
            WithPoints(Day(Sunday.AddDays(-1), 10m), null),
            WithPoints(Day(Sunday, 6m), 0m)
        ], Sunday);
        var series = report.Series[0];
        Assert.Equal(8m, series.Weeks[^1].Average);
        Assert.Equal(6m, series.RatePlusPointsWeeks![^1].Average);
        Assert.Equal(1, series.RatePlusPointsWeeks[^1].Observations);
    }

    [Fact]
    public void RatePlusPointsShowsGapsForMissingPointsOrMissingWeeks()
    {
        var report = WeeklyReportBuilder.Build([
            WithPoints(Day(Sunday.AddDays(-14), 6m), .5m),
            WithPoints(Day(Sunday, 6m), null)
        ], Sunday);
        var points = report.Series[0].RatePlusPointsWeeks!;
        Assert.Equal(6.5m, points[^3].Average);
        Assert.Null(points[^2].Average);
        Assert.Null(points[^1].Average);
        Assert.Equal(0, points[^1].Observations);
    }

    [Fact]
    public void HandlesEmptyHistory() => Assert.Empty(WeeklyReportBuilder.Build([], Sunday).Series);

    [Fact]
    public void YearWindowHandlesLeapDay()
    {
        var report = WeeklyReportBuilder.Build([], new DateOnly(2024, 2, 29));
        Assert.Equal(new DateOnly(2023, 3, 1), report.From);
    }
}
