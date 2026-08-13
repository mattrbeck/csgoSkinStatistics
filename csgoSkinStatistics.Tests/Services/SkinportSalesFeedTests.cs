using System.Text.Json;
using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// Deserialization of Skinport's real /v1/sales/history payload, pinned against records captured
// from the live feed on 2026-08-13 rather than hand-written ones. The model only names the handful
// of fields we use, so this is what would catch Skinport renaming a window, moving `median`, or
// changing how a multi-variant item is split across rows - none of which the hand-written unit
// tests in PriceServiceSaleTests could notice.
//
// The Doppler record is the reason the fixture is real: seven rows share one market_hash_name,
// their sales are scattered unevenly across windows, and the phases differ four-fold in price
// (a Sapphire at $3435 next to a Phase 1 at $807). That is far too awkward a shape to have
// invented, and it is exactly what the pooling rule has to survive.
public class SkinportSalesFeedTests
{
    // Captured verbatim from https://api.skinport.com/v1/sales/history?app_id=730&currency=USD,
    // trimmed to three items. Fields we don't model (min/max/avg, item_page, market_page, currency)
    // are left in deliberately: deserialization must ignore them.
    private const string LiveFeedSample = """
    [
      {"market_hash_name":"AK-47 | Redline (Field-Tested)","version":null,"currency":"USD",
       "item_page":"https://skinport.com/item/ak-47-redline-field-tested",
       "market_page":"https://skinport.com/market?item=Redline&cat=Rifle&type=AK-47",
       "last_24_hours":{"min":28.82,"max":81.94,"avg":42.66,"median":30.55,"volume":5},
       "last_7_days":{"min":24.35,"max":929.81,"avg":58.77,"median":33.13,"volume":66},
       "last_30_days":{"min":24.35,"max":949.47,"avg":51.88,"median":31.77,"volume":302},
       "last_90_days":{"min":21.59,"max":949.47,"avg":47.3,"median":32.82,"volume":1107}},

      {"market_hash_name":"★ M9 Bayonet | Doppler (Minimal Wear)","version":"Black Pearl","currency":"USD",
       "last_24_hours":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_7_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_30_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_90_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0}},
      {"market_hash_name":"★ M9 Bayonet | Doppler (Minimal Wear)","version":"Phase 2","currency":"USD",
       "last_24_hours":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_7_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_30_days":{"min":1219.18,"max":1219.18,"avg":1219.18,"median":1219.18,"volume":1},
       "last_90_days":{"min":922.32,"max":1219.18,"avg":1098.13,"median":1152.89,"volume":3}},
      {"market_hash_name":"★ M9 Bayonet | Doppler (Minimal Wear)","version":"Ruby","currency":"USD",
       "last_24_hours":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_7_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_30_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_90_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0}},
      {"market_hash_name":"★ M9 Bayonet | Doppler (Minimal Wear)","version":"Phase 1","currency":"USD",
       "last_24_hours":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_7_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_30_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_90_days":{"min":807.03,"max":807.03,"avg":807.03,"median":807.03,"volume":1}},
      {"market_hash_name":"★ M9 Bayonet | Doppler (Minimal Wear)","version":"Phase 4","currency":"USD",
       "last_24_hours":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_7_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_30_days":{"min":905.63,"max":905.63,"avg":905.63,"median":905.63,"volume":1},
       "last_90_days":{"min":897.74,"max":1005.97,"avg":936.44,"median":905.63,"volume":3}},
      {"market_hash_name":"★ M9 Bayonet | Doppler (Minimal Wear)","version":"Sapphire","currency":"USD",
       "last_24_hours":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_7_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_30_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_90_days":{"min":3435.92,"max":3435.92,"avg":3435.92,"median":3435.92,"volume":1}},
      {"market_hash_name":"★ M9 Bayonet | Doppler (Minimal Wear)","version":"Phase 3","currency":"USD",
       "last_24_hours":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_7_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_30_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_90_days":{"min":869.79,"max":966,"avg":903.38,"median":874.35,"volume":3}},

      {"market_hash_name":"Sticker Slab | BIG (Gold) | Stockholm 2021","version":null,"currency":"USD",
       "last_24_hours":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_7_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_30_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0},
       "last_90_days":{"min":null,"max":null,"avg":null,"median":null,"volume":0}}
    ]
    """;

    private static List<SkinportSalesItem> Parse() =>
        JsonSerializer.Deserialize<List<SkinportSalesItem>>(LiveFeedSample)!;

    private static List<SkinportSalesItem> Rows(string name) =>
        Parse().Where(r => r.market_hash_name == name).ToList();

    [Fact]
    public void DeserializesTheLiveSchema()
    {
        var rows = Parse();
        Assert.Equal(9, rows.Count);

        var ak = rows[0];
        Assert.Equal("AK-47 | Redline (Field-Tested)", ak.market_hash_name);
        Assert.Null(ak.version);
        Assert.Equal(30.55, ak.last_24_hours!.median);
        Assert.Equal(5, ak.last_24_hours.volume);
        Assert.Equal(1107, ak.last_90_days!.volume);

        // The variant marker we pool on.
        Assert.Equal("Sapphire", rows.Single(r => r.last_90_days?.median == 3435.92).version);
    }

    [Fact]
    public void BusyItemTakesTheFreshestWindow()
    {
        // Five sales in 24 hours clears the confidence bar, so we report today's median ($30.55)
        // rather than diluting it with 1107 sales stretching back three months ($32.82).
        var chosen = PriceService.ChooseSale(Rows("AK-47 | Redline (Field-Tested)"));

        Assert.Equal("24h", chosen!.Value.Window);
        Assert.Equal(3055, chosen.Value.MedianCents);
        Assert.Equal(5, chosen.Value.Volume);
        Assert.False(chosen.Value.Pooled);
    }

    [Fact]
    public void DopplerPhasesPoolIntoOneVolumeWeightedValue()
    {
        // Seven rows, one name. The 30d window holds only two sales across all phases, which is
        // too thin, so we widen to 90d and pool its eleven: three phases at ~$900-1150, a lone
        // Phase 1 at $807 and a lone Sapphire at $3435. Weighting by volume keeps the single
        // Sapphire sale from dragging the whole knife up to gem money.
        var chosen = PriceService.ChooseSale(Rows("★ M9 Bayonet | Doppler (Minimal Wear)"));

        Assert.Equal("90d", chosen!.Value.Window);
        Assert.Equal(11, chosen.Value.Volume);
        Assert.True(chosen.Value.Pooled);
        // (1152.89*3 + 807.03 + 905.63*3 + 3435.92 + 874.35*3) / 11 = $1185.60
        Assert.Equal(118560, chosen.Value.MedianCents);

        // Sanity: the pooled value sits among the common phases, not up at the Sapphire.
        Assert.InRange(chosen.Value.MedianCents, 80703, 343592);
    }

    [Fact]
    public void ItemWithNoSalesInNinetyDaysYieldsNothing()
    {
        // The caller must then keep whatever median it already had indexed for this name.
        Assert.Null(PriceService.ChooseSale(Rows("Sticker Slab | BIG (Gold) | Stockholm 2021")));
    }
}
