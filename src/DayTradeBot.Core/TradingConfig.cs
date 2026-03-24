namespace DayTradeBot.Core;

public class TradingConfig
{
    /// <summary>DryRun / PaperTrade / Live</summary>
    public string Mode { get; set; } = "DryRun";

    /// <summary>單邊手續費率 0.1425%</summary>
    public decimal CommissionRate { get; set; } = 0.001425m;

    /// <summary>手續費折讓（預設 5 折）</summary>
    public decimal CommissionDiscount { get; set; } = 0.5m;

    /// <summary>單邊最低手續費（元）</summary>
    public decimal MinCommission { get; set; } = 20m;

    /// <summary>當沖交易稅率 0.15%（一般賣出為 0.3%，當沖減半）</summary>
    public decimal TaxRate { get; set; } = 0.0015m;

    /// <summary>模擬交易初始本金</summary>
    public decimal InitialCapital { get; set; } = 1_000_000m;

    public bool IsLive       => Mode.Equals("Live",       StringComparison.OrdinalIgnoreCase);
    public bool IsPaperTrade => Mode.Equals("PaperTrade", StringComparison.OrdinalIgnoreCase);
    public bool IsDryRun     => !IsLive && !IsPaperTrade;

    /// <summary>計算雙邊手續費（整股，qty 單位為股）</summary>
    public decimal CalcCommission(decimal entryPrice, decimal exitPrice, long qtyShares)
    {
        var buyFee  = Math.Max(MinCommission,
            Math.Round(entryPrice * qtyShares * CommissionRate * CommissionDiscount, 0));
        var sellFee = Math.Max(MinCommission,
            Math.Round(exitPrice  * qtyShares * CommissionRate * CommissionDiscount, 0));
        return buyFee + sellFee;
    }

    /// <summary>計算當沖交易稅（賣出金額 × 0.15%）</summary>
    public decimal CalcTax(decimal exitPrice, long qtyShares) =>
        Math.Round(exitPrice * qtyShares * TaxRate, 0);
}
