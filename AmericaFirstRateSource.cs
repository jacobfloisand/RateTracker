using System.Globalization;
using HtmlAgilityPack;

namespace MortgageRateTracker;

public sealed class AmericaFirstRateSource(HttpClient httpClient)
{
    public const string SourceUrl =
        "https://www.americafirst.com/loans/mortgage-loans/mortgage-loans.html#rates";

    private static readonly string[] Products =
        ["10 Year Fixed", "15 Year Fixed", "20 Year Fixed", "30 Year Fixed"];

    public async Task<MortgageRateSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var html = await httpClient.GetStringAsync(SourceUrl, cancellationToken);
        return Parse(html);
    }

    public static MortgageRateSnapshot Parse(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var section = document.GetElementbyId("rates")
            ?? throw new InvalidDataException("America First's mortgage rates section was not found.");

        // Only inspect tables under Current Rates. Stop matching when another heading begins.
        // This excludes the promotional table, mobile accordion, and Government Rates table.
        var inCurrentRates = false;
        var tables = new List<HtmlNode>();
        foreach (var node in section.Descendants().Where(n => n.Name is "h2" or "table"))
        {
            if (node.Name == "h2")
                inCurrentRates = Text(node).Equals("Current Rates", StringComparison.OrdinalIgnoreCase);
            else if (inCurrentRates)
                tables.Add(node);
        }

        if (tables.Count != 1)
            throw new InvalidDataException("Expected exactly one Current Rates table on America First's page.");

        var table = tables[0];
        var headers = table.SelectNodes("./thead/tr/th")?.Select(Text).ToArray() ?? [];
        var productColumn = Column(headers, "PRODUCT");
        var interestColumn = Column(headers, "INTEREST RATE");
        var discountColumn = Column(headers, "DISCOUNT POINTS");
        var aprColumn = Column(headers, "APR* (AS LOW AS)");
        var rates = new Dictionary<string, MortgageRate>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in table.SelectNodes("./tbody/tr")?.ToArray() ?? [])
        {
            var cells = row.SelectNodes("./td")?.Select(Text).ToArray() ?? [];
            if (cells.Length != headers.Length)
                throw new InvalidDataException("A Current Rates row has an unexpected number of columns.");

            var product = cells[productColumn];
            if (!Products.Contains(product, StringComparer.OrdinalIgnoreCase))
                continue;

            var rate = new MortgageRate(product,
                Percentage(cells[interestColumn], product, "interest rate"),
                Percentage(cells[discountColumn], product, "discount points"),
                Percentage(cells[aprColumn], product, "APR"));

            if (!rates.TryAdd(product, rate))
                throw new InvalidDataException($"Duplicate Current Rates product: {product}.");
        }

        var missing = Products.Where(product => !rates.ContainsKey(product)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"Missing Current Rates products: {string.Join(", ", missing)}.");

        var component = table.Ancestors().FirstOrDefault(node =>
            node.GetAttributeValue("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("cmp-mortgage-rates-table"));
        var effectiveDate = component?.Descendants("p")
            .FirstOrDefault(node => Text(node).Contains("All rates are effective as of", StringComparison.OrdinalIgnoreCase))
            ?.SelectSingleNode(".//strong");

        return new MortgageRateSnapshot(Products.Select(product => rates[product]).ToArray(),
            effectiveDate is null ? null : Text(effectiveDate));
    }

    private static int Column(string[] headers, string name)
    {
        var matches = headers.Select((header, index) => (header, index))
            .Where(item => item.header.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].index
            : throw new InvalidDataException($"Expected one '{name}' column in Current Rates.");
    }

    private static decimal Percentage(string text, string product, string field)
    {
        if (!text.EndsWith('%') ||
            !decimal.TryParse(text[..^1].Trim(), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) || value < 0 || value > 100)
            throw new InvalidDataException($"Invalid {field} for {product}: '{text}'.");

        return value;
    }

    private static string Text(HtmlNode node) =>
        string.Join(" ", HtmlEntity.DeEntitize(node.InnerText).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
