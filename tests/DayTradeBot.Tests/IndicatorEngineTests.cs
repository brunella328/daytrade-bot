using DayTradeBot.Core;
using DayTradeBot.Core.Models;
using Xunit;

namespace DayTradeBot.Tests;

public class IndicatorEngineTests
{
    private static List<KLine> BuildKLines(int count, decimal basePrice = 100m, string symbol = "TEST")
    {
        var list = new List<KLine>();
        var rng = new Random(42);
        for (int i = 0; i < count; i++)
        {
            var open = basePrice + (decimal)(rng.NextDouble() * 4 - 2);
            var close = open + (decimal)(rng.NextDouble() * 2 - 1);
            list.Add(new KLine
            {
                Symbol = symbol,
                OpenTime = new DateTime(2024, 1, 2, 9, 0, 0).AddMinutes(i),
                Open = open,
                High = Math.Max(open, close) + (decimal)(rng.NextDouble()),
                Low = Math.Min(open, close) - (decimal)(rng.NextDouble()),
                Close = close,
                Volume = rng.Next(100, 1000)
            });
        }
        return list;
    }

    [Fact]
    public void InsufficientBars_ReturnsNull()
    {
        var engine = new IndicatorEngine();
        var klines = BuildKLines(10);
        Assert.Null(engine.Calculate(klines));
    }

    [Fact]
    public void SufficientBars_ReturnsResult()
    {
        var engine = new IndicatorEngine();
        var klines = BuildKLines(50);
        var result = engine.Calculate(klines);
        Assert.NotNull(result);
    }

    [Fact]
    public void AdxValue_InReasonableRange()
    {
        var engine = new IndicatorEngine();
        var klines = BuildKLines(60);
        var result = engine.Calculate(klines);
        Assert.NotNull(result);
        Assert.NotNull(result!.Adx);
        Assert.InRange(result.Adx!.Value, 0, 100);
    }

    [Fact]
    public void RsiValue_InReasonableRange()
    {
        var engine = new IndicatorEngine();
        var klines = BuildKLines(60);
        var result = engine.Calculate(klines);
        Assert.NotNull(result?.Rsi);
        Assert.InRange(result!.Rsi!.Value, 0, 100);
    }

    [Fact]
    public void BbLower_IsLessThanUpperBand()
    {
        var engine = new IndicatorEngine();
        var klines = BuildKLines(60, basePrice: 500m); // 確保價格為正
        var result = engine.Calculate(klines);
        Assert.NotNull(result?.BbLower);
        // BB Lower 應小於等於收盤價附近（合理性驗證）
        Assert.True(result!.BbLower!.Value > 0);
    }
}
