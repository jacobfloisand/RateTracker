using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MortgageRateTracker;

public sealed class CityCreekRateSource(HttpClient httpClient)
{
    public const string SourceUrl = "https://citycreekmortgage.com/todays-rates/";

    private const string LoanPattern =
        @"(?<interest>\d+(?:\.\d+)?)\s*%\s*\(\s*(?<apr>\d+(?:\.\d+)?)\s*%\s+APR\s*\)\s+for\s+a\s+(?<term>\d+)\s*[-–‑]\s*year\s+fixed(?:\s*[-–‑]\s*|\s+)rate\s+(?:mortgage|loan)";

    private static readonly Regex SummaryPattern = new(
        @"^As of (?<date>[A-Za-z]+ \d{1,2}, \d{4}), the rates in Utah are " +
        LoanPattern + @"\s+and\s+" + LoanPattern + @"\.?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public async Task<MortgageRateSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var html = await httpClient.GetStringAsync(SourceUrl, cancellationToken);
        return Parse(html);
    }

    public static MortgageRateSnapshot Parse(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//noscript|//template")?.ToArray() ?? [])
            node.Remove();

        // Read the published summary paragraph, separate from rate cards and historical chart data.
        var summaries = document.DocumentNode.Descendants("p")
            .Select(node => string.Join(" ", HtmlEntity.DeEntitize(node.InnerText)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(text => text.StartsWith("As of ", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("rates in Utah are", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (summaries.Length != 1)
            throw new InvalidDataException("Expected exactly one City Creek Utah rate summary paragraph.");

        var match = SummaryPattern.Match(summaries[0]);
        if (!match.Success)
            throw new InvalidDataException("City Creek's rate summary format changed or contains invalid rates.");

        var dateText = match.Groups["date"].Value;
        if (!DateOnly.TryParseExact(dateText, "MMMM d, yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            throw new InvalidDataException($"Invalid date in City Creek's rate summary: '{dateText}'.");

        var rates = new Dictionary<string, MortgageRate>();
        for (var i = 0; i < match.Groups["term"].Captures.Count; i++)
        {
            var term = match.Groups["term"].Captures[i].Value;
            if (term is not ("15" or "30"))
                throw new InvalidDataException($"Unexpected City Creek mortgage term: '{term}'.");

            var rate = new MortgageRate($"{term} Year Fixed",
                Percentage(match.Groups["interest"].Captures[i].Value),
                null,
                Percentage(match.Groups["apr"].Captures[i].Value));
            if (!rates.TryAdd(term, rate))
                throw new InvalidDataException($"Duplicate City Creek mortgage term: '{term}'.");
        }

        if (!rates.ContainsKey("15") || !rates.ContainsKey("30"))
            throw new InvalidDataException("City Creek's summary must include both 15- and 30-year rates.");

        return new MortgageRateSnapshot([rates["15"], rates["30"]], dateText);
    }

    private static decimal Percentage(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out var value) || value < 0 || value > 100)
            throw new InvalidDataException($"Invalid percentage in City Creek's rate summary: '{text}'.");
        return value;
    }
}
