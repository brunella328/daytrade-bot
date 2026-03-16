namespace DayTradeBot.Core;

/// <summary>
/// 台灣股市合法跳動單位（升降單位）計算。
/// 依金管會規定，依股價區間四捨五入到最小跳動單位。
/// </summary>
public static class TickSizeHelper
{
    public static decimal GetTickSize(decimal price) => price switch
    {
        < 10m      => 0.01m,
        < 50m      => 0.05m,
        < 100m     => 0.10m,
        < 500m     => 0.50m,
        < 1000m    => 1.00m,
        _          => 5.00m
    };

    /// <summary>將價格 round 到最近的合法跳動單位（四捨五入）</summary>
    public static decimal RoundToTickSize(decimal price)
    {
        var tick = GetTickSize(price);
        return Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick;
    }

    /// <summary>漲停價 = 前收盤 × 1.1，取合法跳動單位（無條件捨去）</summary>
    public static decimal UpperLimit(decimal previousClose)
    {
        var raw = previousClose * 1.1m;
        var tick = GetTickSize(raw);
        return Math.Floor(raw / tick) * tick;
    }

    /// <summary>跌停價 = 前收盤 × 0.9，取合法跳動單位（無條件進位）</summary>
    public static decimal LowerLimit(decimal previousClose)
    {
        var raw = previousClose * 0.9m;
        var tick = GetTickSize(raw);
        return Math.Ceiling(raw / tick) * tick;
    }
}
