using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// The sale-price index: where the number on an inventory card actually comes from.
//
// Two decisions carry the whole feature and both are pure functions, so they are pinned directly.
// ChooseSale turns Skinport's four nested sales windows into one median, and ResolveExact ranks a
// sale against a listing for the same item. The ranking is the part with teeth: a listing is a
// price at which the item demonstrably did NOT sell, so it must lose to a real sale, and it must
// also lose to a *stale* sale rather than the other way round, because an old price for the right
// item beats a current price for a different one.
public class PriceServiceSaleTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    private static SkinportSalesItem Row(
        string name,
        (double? Median, int Volume)? h24 = null,
        (double? Median, int Volume)? d7 = null,
        (double? Median, int Volume)? d30 = null,
        (double? Median, int Volume)? d90 = null,
        string? version = null)
    {
        static SkinportSalesWindow W((double? Median, int Volume)? w) =>
            new() { median = w?.Median, volume = w?.Volume ?? 0 };
        return new SkinportSalesItem
        {
            market_hash_name = name,
            version = version,
            last_24_hours = W(h24),
            last_7_days = W(d7),
            last_30_days = W(d30),
            last_90_days = W(d90),
        };
    }

    // ---- ChooseSale: which window, and how thin samples are handled ----

    [Fact]
    public void ChooseSale_PrefersNarrowestWindowWithEnoughVolume()
    {
        // 24h has a real sample, so we don't dilute it with three months of older sales.
        var chosen = PriceService.ChooseSale([Row("AK-47 | Redline (Field-Tested)",
            h24: (10.00, 5), d7: (11.00, 40), d30: (12.00, 200), d90: (13.00, 900))]);

        Assert.NotNull(chosen);
        Assert.Equal("24h", chosen!.Value.Window);
        Assert.Equal(1000, chosen.Value.MedianCents);
        Assert.Equal(5, chosen.Value.Volume);
    }

    [Fact]
    public void ChooseSale_WidensWhenNarrowWindowIsTooThin()
    {
        // A single 24h sale is not a median - one odd float would set it - so we widen to the 7d
        // window, which contains that sale and 29 more.
        var chosen = PriceService.ChooseSale([Row("AWP | Asiimov (Field-Tested)",
            h24: (99.00, 1), d7: (60.00, 30), d30: (61.00, 120))]);

        Assert.Equal("7d", chosen!.Value.Window);
        Assert.Equal(6000, chosen.Value.MedianCents);
    }

    [Fact]
    public void ChooseSale_FallsBackToWidestWindowWithAnySales()
    {
        // The rarely-traded case this whole feature exists for: two sales in three months and
        // nothing since. Better than any asking price, so we take it rather than give up.
        var chosen = PriceService.ChooseSale([Row("Souvenir AWP | Dragon Lore (Factory New)",
            d90: (25000.00, 2))]);

        Assert.Equal("90d", chosen!.Value.Window);
        Assert.Equal(2500000, chosen.Value.MedianCents);
        Assert.Equal(2, chosen.Value.Volume);
        Assert.False(chosen.Value.Pooled);
    }

    [Fact]
    public void ChooseSale_ReturnsNullWhenNothingSold()
    {
        // Every window empty. The caller must leave any previously indexed median in place rather
        // than overwrite it with nothing.
        Assert.Null(PriceService.ChooseSale([Row("Sticker | Nobody Wants This")]));
    }

    [Fact]
    public void ChooseSale_IgnoresWindowsWithVolumeButNoMedian()
    {
        var chosen = PriceService.ChooseSale([Row("Glock-18 | Fade (Factory New)",
            h24: (null, 4), d30: (500.00, 9))]);

        Assert.Equal("30d", chosen!.Value.Window);
        Assert.Equal(50000, chosen.Value.MedianCents);
    }

    // ---- ChooseSale: pooling names that hide a sub-variant ----

    [Fact]
    public void ChooseSale_PoolsVariantsSharingOneNameVolumeWeighted()
    {
        // Doppler phases share a market_hash_name and Steam gives us no way to tell which one an
        // inventory item is, so the honest answer is the volume-weighted expectation across them,
        // flagged pooled so the UI can mark it approximate.
        var chosen = PriceService.ChooseSale([
            Row("★ Bayonet | Doppler (Factory New)", d30: (900.00, 3), version: "Phase 1"),
            Row("★ Bayonet | Doppler (Factory New)", d30: (1100.00, 1), version: "Sapphire"),
        ]);

        Assert.True(chosen!.Value.Pooled);
        Assert.Equal(4, chosen.Value.Volume);
        // (900*3 + 1100*1) / 4 = 950
        Assert.Equal(95000, chosen.Value.MedianCents);
    }

    [Fact]
    public void ChooseSale_PoolingCombinesVolumeToClearTheConfidenceBar()
    {
        // Two phases with two sales each: neither clears the bar alone, together they do, so we
        // stay on the narrow window instead of needlessly widening.
        var chosen = PriceService.ChooseSale([
            Row("★ Karambit | Doppler (Factory New)", d7: (1000.00, 2), d90: (500.00, 50), version: "Phase 2"),
            Row("★ Karambit | Doppler (Factory New)", d7: (1000.00, 2), d90: (500.00, 50), version: "Phase 4"),
        ]);

        Assert.Equal("7d", chosen!.Value.Window);
        Assert.Equal(4, chosen.Value.Volume);
        Assert.Equal(100000, chosen.Value.MedianCents);
    }

    [Fact]
    public void ChooseSale_SingleRowIsNotPooled()
    {
        var chosen = PriceService.ChooseSale([Row("AK-47 | Redline (Field-Tested)", d7: (10.00, 20))]);
        Assert.False(chosen!.Value.Pooled);
    }

    // ---- ResolveExact: the ranking ----

    private static SaleStat Sale(int cents, int volume = 10, string window = "7d", bool pooled = false, int ageDays = 0) =>
        new(cents, volume, window, pooled, Now.AddDays(-ageDays));

    private static SkinPrice Listing(int? min, int? suggested, int ageDays = 0) =>
        new(min, suggested, Now.AddDays(-ageDays));

    [Fact]
    public void ResolveExact_PrefersRecentSaleOverListing()
    {
        // The headline claim: an asking price of $125 loses to a measured sale median of $100.
        var result = PriceService.ResolveExact(Sale(10000), Listing(11000, 12500), Now);

        Assert.Equal(PriceBasis.Sale, result!.Basis);
        Assert.Equal(10000, result.ValueCents);
        Assert.False(result.Approximate);
        // The listing detail still rides along for callers that want it.
        Assert.Equal(11000, result.MinCents);
        Assert.Equal(12500, result.SuggestedCents);
    }

    [Fact]
    public void ResolveExact_PrefersStaleSaleOverNothing()
    {
        // Aged out of every window and no live listing: still the right item, so we show it and
        // mark it approximate rather than falling through to a different wear.
        var result = PriceService.ResolveExact(Sale(10000, ageDays: 200), null, Now);

        Assert.Equal(PriceBasis.StaleSale, result!.Basis);
        Assert.Equal(10000, result.ValueCents);
        Assert.True(result.Approximate);
    }

    [Fact]
    public void ResolveExact_PrefersLiveListingOverStaleSale()
    {
        // Once a sale is old enough that we haven't seen this item trade in 90+ days, a live ask
        // is the more current signal - it just isn't a sale, so the basis says listing.
        var result = PriceService.ResolveExact(Sale(10000, ageDays: 200), Listing(11000, 12500), Now);

        Assert.Equal(PriceBasis.Listing, result!.Basis);
        Assert.Equal(12500, result.ValueCents);
    }

    [Fact]
    public void ResolveExact_FlagsThinAndPooledSamplesApproximate()
    {
        Assert.True(PriceService.ResolveExact(Sale(10000, volume: 1), null, Now)!.Approximate);
        Assert.True(PriceService.ResolveExact(Sale(10000, pooled: true), null, Now)!.Approximate);
        Assert.False(PriceService.ResolveExact(Sale(10000, volume: 2), null, Now)!.Approximate);
    }

    [Fact]
    public void ResolveExact_StaleListingStaysApproximate()
    {
        // Pre-existing behaviour, unchanged by the sale index: a listing that aged out of the feed
        // is still shown, still with a "~".
        var result = PriceService.ResolveExact(null, Listing(11000, 12500, ageDays: 30), Now);

        Assert.Equal(PriceBasis.Listing, result!.Basis);
        Assert.True(result.Approximate);
    }

    [Fact]
    public void ResolveExact_IgnoresListingWithNoSuggestedPrice()
    {
        // quantity 0 with no smoothed reference is not a usable price; it must not mask a sale.
        var result = PriceService.ResolveExact(Sale(10000, ageDays: 200), Listing(null, null), Now);

        Assert.Equal(PriceBasis.StaleSale, result!.Basis);
    }

    [Fact]
    public void ResolveExact_ReturnsNullWhenNothingIsKnown()
    {
        // Only then may the caller borrow a neighbouring wear.
        Assert.Null(PriceService.ResolveExact(null, null, Now));
    }
}
