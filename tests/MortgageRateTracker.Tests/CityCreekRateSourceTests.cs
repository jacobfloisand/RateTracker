using System.Globalization;
using System.Net;

namespace MortgageRateTracker.Tests;

public class CityCreekRateSourceTests
{
    private const string Summary = "As of September 27, 2026, the rates in Utah are " +
        "6.999% (7.111% APR) for a 30-year fixed rate mortgage and " +
        "6.375% (6.375% APR) for a 15-year fixed-rate loan.";

    [Fact]
    public void ParsesSummaryDateAndBothProductsWithoutInventingDiscountPoints()
    {
        var result = CityCreekRateSource.Parse(Page(Summary));
        Assert.Equal("September 27, 2026", result.EffectiveAsOf);
        Assert.Equal(new[]
        {
            new MortgageRate("15 Year Fixed", 6.375m, null, 6.375m),
            new MortgageRate("30 Year Fixed", 6.999m, null, 7.111m)
        }, result.Rates);
    }

    [Fact]
    public void HandlesInlineMarkupEntitiesWhitespaceAndHyphens()
    {
        var text = Summary.Replace("the rates", "the&nbsp;\n rates")
            .Replace("6.999%", "<span>6.999<sup>%</sup></span>")
            .Replace("30-year", "30&#8209;year")
            .Replace("fixed-rate", "fixed rate");
        Assert.Equal(6.999m, CityCreekRateSource.Parse(Page(text)).Rates[1].InterestRate);
    }

    [Fact]
    public void ParsesUpdatedValuesIndependentlyOfCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var text = Summary.Replace("September 27, 2026", "October 1, 2026")
                .Replace("6.999%", "5.25%").Replace("7.111%", "5.5%");
            var result = CityCreekRateSource.Parse(Page(text));
            Assert.Equal("October 1, 2026", result.EffectiveAsOf);
            Assert.Equal(5.25m, result.Rates[1].InterestRate);
            Assert.Equal(5.5m, result.Rates[1].Apr);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("6.999%", "N/A")]
    [InlineData("6.999%", "-1%")]
    [InlineData("7.111%", "101%")]
    [InlineData("7.111%", "7.111")]
    [InlineData("15-year", "30-year")]
    [InlineData("15-year", "20-year")]
    [InlineData("September 27", "September 31")]
    [InlineData("rates in Utah are", "rates in Nevada are")]
    public void RejectsInvalidOrUnexpectedSummary(string original, string replacement)
    {
        Assert.Throws<InvalidDataException>(() => CityCreekRateSource.Parse(
            Page(Summary.Replace(original, replacement))));
    }

    [Fact]
    public void RejectsMissingProduct()
    {
        Assert.Throws<InvalidDataException>(() => CityCreekRateSource.Parse(Page(Summary.Split(" and ")[0] + ".")));
    }

    [Fact]
    public void RejectsMissingSummaryInsteadOfUsingScriptOrOtherRates()
    {
        Assert.Throws<InvalidDataException>(() => CityCreekRateSource.Parse(
            $"<html><body><script>var summary = '<p>{Summary}</p>';</script><p>30-year: 5% APR</p></body></html>"));
    }

    [Fact]
    public void RejectsAmbiguousSummaries()
    {
        Assert.Throws<InvalidDataException>(() => CityCreekRateSource.Parse(
            Page(Summary) + $"<p>{Summary.Replace("6.999%", "5.999%")}</p>"));
    }

    [Fact]
    public async Task FetchesAndParsesHttpResponse()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK));
        Assert.Equal(2, (await new CityCreekRateSource(client).FetchAsync()).Rates.Count);
    }

    [Fact]
    public async Task RejectsHttpFailure()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => new CityCreekRateSource(client).FetchAsync());
    }

    private static string Page(string summary) => $"""
        <html><body>
          <div>30 Yr Conventional Fixed 5.99% APR 6.5%</div>
          <h2>Current Mortgage and Refinance Rates in Utah</h2>
          <p><span><strong>{summary}</strong></span></p>
          <script>var rateHistory = [5.25, 6.5];</script>
          <h2>Rate History</h2><p>Rates as of January 1, 2020: 3.5%</p>
        </body></html>
        """;

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(CityCreekRateSource.SourceUrl, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(Page(Summary)) });
        }
    }
}
