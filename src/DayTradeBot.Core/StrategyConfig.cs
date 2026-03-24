namespace DayTradeBot.Core;

/// <summary>
/// 策略參數，可透過 API 即時調整（不需重啟）。
/// </summary>
public class StrategyConfig
{
    /// <summary>ADX 門檻：低於此值才進場（低趨勢市場）</summary>
    public double AdxThreshold { get; set; } = 25;

    /// <summary>RSI 門檻：低於此值才進場（超賣）</summary>
    public double RsiThreshold { get; set; } = 30;

    /// <summary>是否啟用 BB 下軌條件（收盤價需低於下軌）</summary>
    public bool UseBbCondition { get; set; } = true;

    /// <summary>
    /// 深水區禁區門檻：當前跌幅超過此比例時，禁止任何買進（預設 7%）。
    /// 防止在跌停鎖死前進場，停損單無法成交。
    /// </summary>
    public double RedZoneThreshold { get; set; } = 0.07;

    /// <summary>停利比例（0.008 = 0.8%）</summary>
    public decimal TakeProfitPct { get; set; } = 0.008m;

    /// <summary>停損比例（0.010 = 1.0%）</summary>
    public decimal StopLossPct { get; set; } = 0.010m;

    /// <summary>允許進場的開始時間（24h，預設 9.25 = 09:15）</summary>
    public double EntryStartHour { get; set; } = 9.25;

    /// <summary>允許進場的結束時間（24h，預設 13）</summary>
    public double EntryEndHour { get; set; } = 13;

    /// <summary>強制平倉結束時間（24h，預設 13.5）</summary>
    public double ForceCloseHour { get; set; } = 13.5;

    // ── 部位規模 ──────────────────────────────────────────────────────────

    /// <summary>總資金（用於計算倉位大小）</summary>
    public decimal TotalCapital { get; set; } = 1_000_000m;

    /// <summary>單筆最大投入比例（預設 20%）</summary>
    public decimal MaxPositionPct { get; set; } = 0.20m;

    /// <summary>全域同時持倉上限（預設 2）</summary>
    public int MaxConcurrentPositions { get; set; } = 2;

    /// <summary>
    /// 計算買進張數（1 張 = 1000 股）。
    /// 回傳 0 表示資金不足 1 張，應跳過不進場。
    /// </summary>
    public long CalcLots(decimal price)
    {
        if (price <= 0) return 0;
        var maxAmount = TotalCapital * MaxPositionPct;           // e.g. 200,000
        var lots = (long)Math.Floor((double)(maxAmount / (price * 1000)));
        return lots;
    }
}
