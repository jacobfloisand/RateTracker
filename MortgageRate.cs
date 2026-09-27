namespace MortgageRateTracker;

// Values are percentage points: 6.375 means 6.375%, not 0.06375.
// Null discount points means the source does not provide them.
public sealed record MortgageRate(
    string Product,
    decimal InterestRate,
    decimal? DiscountPoints,
    decimal Apr);

public sealed record MortgageRateSnapshot(
    IReadOnlyList<MortgageRate> Rates,
    string? EffectiveAsOf);
