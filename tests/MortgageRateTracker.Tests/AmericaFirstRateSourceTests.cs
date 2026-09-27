using System.Net;

namespace MortgageRateTracker.Tests;

public class AmericaFirstRateSourceTests
{
    [Fact]
    public void ParsesOnlyCurrentRatesWithDecodedWhitespaceAndReorderedColumns()
    {
        var snapshot = AmericaFirstRateSource.Parse(Page());

        Assert.Equal(new[] { "10 Year Fixed", "15 Year Fixed", "20 Year Fixed", "30 Year Fixed" },
            snapshot.Rates.Select(rate => rate.Product));
        Assert.All(snapshot.Rates, rate =>
        {
            Assert.Equal(6.125m, rate.InterestRate);
            Assert.Equal(0.5m, rate.DiscountPoints);
            Assert.Equal(6.345m, rate.Apr);
        });
        Assert.Equal("Friday, September 25, 2026 02:50 PM MST", snapshot.EffectiveAsOf);
    }

    [Fact]
    public void RejectsMissingProductEvenWhenGovernmentTableContainsIt()
    {
        Assert.Throws<InvalidDataException>(() => AmericaFirstRateSource.Parse(
            Page(Table([10, 15, 20]))));
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("6.125")]
    [InlineData("-1%")]
    [InlineData("101%")]
    public void RejectsInvalidPercentage(string value)
    {
        Assert.Throws<InvalidDataException>(() => AmericaFirstRateSource.Parse(
            Page(Table([10, 15, 20, 30], value))));
    }

    [Fact]
    public void RejectsDuplicateProduct()
    {
        Assert.Throws<InvalidDataException>(() => AmericaFirstRateSource.Parse(
            Page(Table([10, 15, 20, 30, 30]))));
    }

    [Theory]
    [InlineData("Current Rates", "Other Rates")]
    [InlineData("DISCOUNT POINTS", "FEES")]
    [InlineData("id='rates'", "id='other'")]
    public void RejectsChangedPageStructure(string original, string replacement)
    {
        Assert.Throws<InvalidDataException>(() => AmericaFirstRateSource.Parse(
            Page().Replace(original, replacement)));
    }

    [Fact]
    public void AllowsAbsentEffectiveDate()
    {
        var html = Page().Replace("All rates are effective as of", "Updated");
        Assert.Null(AmericaFirstRateSource.Parse(html).EffectiveAsOf);
    }

    [Fact]
    public async Task FetchesAndParsesHttpResponse()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, Page()));
        var snapshot = await new AmericaFirstRateSource(client).FetchAsync();
        Assert.Equal(4, snapshot.Rates.Count);
    }

    [Fact]
    public async Task RejectsUnsuccessfulHttpResponse()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable, Page()));
        await Assert.ThrowsAsync<HttpRequestException>(() => new AmericaFirstRateSource(client).FetchAsync());
    }

    private static string Page(string? currentTable = null) => $"""
        <html><body>
          {Table([10, 15, 20, 30], "99%")}
          <div id='rates'>
            <h2> Current Rates </h2>
            <div class='cmp-mortgage-rates-table'>
              <div><button>10 Year Fixed</button><span>99%</span></div>
              {currentTable ?? Table([30, 10, 20, 15])}
              <p>All rates are effective as of <strong>Friday, September 25, 2026 02:50 PM MST</strong>.</p>
            </div>
            <h2>Government Rates</h2>
            {Table([10, 15, 20, 30], "99%")}
          </div>
        </body></html>
        """;

    private static string Table(int[] years, string interest = "6.125%") => $"""
        <table>
          <thead><tr><th>PRODUCT</th><th>APR* (AS LOW AS)</th><th>DISCOUNT POINTS</th><th>INTEREST RATE</th></tr></thead>
          <tbody>{string.Join("", years.Select(year => $"<tr><td>{year}&nbsp;Year\n Fixed</td><td><span>6.345%</span></td><td>0.5%</td><td>{interest}</td></tr>"))}</tbody>
        </table>
        """;

    private sealed class StubHandler(HttpStatusCode statusCode, string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(AmericaFirstRateSource.SourceUrl, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(html) });
        }
    }
}
