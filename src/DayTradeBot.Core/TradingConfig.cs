namespace DayTradeBot.Core;

public class TradingConfig
{
    /// <summary>DryRun（模擬）或 Live（真實下單）</summary>
    public string Mode { get; set; } = "DryRun";

    /// <summary>單邊手續費率（預設 0.1425%，可依實際折扣調整）</summary>
    public decimal CommissionRate { get; set; } = 0.001425m;

    /// <summary>賣出交易稅率（固定 0.3%）</summary>
    public decimal TaxRate { get; set; } = 0.003m;

    public bool IsLive => Mode.Equals("Live", StringComparison.OrdinalIgnoreCase);
    public bool IsDryRun => !IsLive;
}
