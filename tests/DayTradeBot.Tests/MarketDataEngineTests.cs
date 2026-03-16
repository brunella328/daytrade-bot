using DayTradeBot.Core;
using DayTradeBot.Core.Models;
using Xunit;

namespace DayTradeBot.Tests;

public class MarketDataEngineTests
{
    [Fact]
    public async Task SingleMinute_AggregatesToOneKLine()
    {
        var engine = new MarketDataEngine();
        KLine? closed = null;
        engine.OnKLineClosed += (_, k) => closed = k;

        var t0 = new DateTime(2024, 1, 2, 9, 0, 0);
        engine.EnqueueTick(new TickData("2330", 780m, 100, t0));
        engine.EnqueueTick(new TickData("2330", 785m, 200, t0.AddSeconds(30)));
        engine.EnqueueTick(new TickData("2330", 778m, 150, t0.AddSeconds(50)));

        // 跨越分鐘邊界 → 觸發收盤
        engine.EnqueueTick(new TickData("2330", 782m, 120, t0.AddMinutes(1)));

        var cts = new CancellationTokenSource(200);
        await engine.StartAsync(cts.Token);
        await Task.Delay(300);

        Assert.NotNull(closed);
        Assert.Equal("2330", closed!.Symbol);
        Assert.Equal(780m, closed.Open);
        Assert.Equal(785m, closed.High);
        Assert.Equal(778m, closed.Low);
        Assert.Equal(778m, closed.Close); // 最後一筆是 778
        Assert.Equal(450L, closed.Volume);
    }

    [Fact]
    public async Task MultipleSymbols_IndependentCandles()
    {
        var engine = new MarketDataEngine();
        var closed = new List<KLine>();
        engine.OnKLineClosed += (_, k) => closed.Add(k);

        var t0 = new DateTime(2024, 1, 2, 9, 0, 0);
        engine.EnqueueTick(new TickData("2330", 780m, 100, t0));
        engine.EnqueueTick(new TickData("2317", 105m, 200, t0));
        engine.EnqueueTick(new TickData("2330", 785m, 100, t0.AddMinutes(1)));
        engine.EnqueueTick(new TickData("2317", 107m, 200, t0.AddMinutes(1)));

        var cts = new CancellationTokenSource(200);
        await engine.StartAsync(cts.Token);
        await Task.Delay(300);

        Assert.Equal(2, closed.Count);
        Assert.Contains(closed, k => k.Symbol == "2330");
        Assert.Contains(closed, k => k.Symbol == "2317");
    }

    [Fact]
    public async Task OhlcvCorrect_MultiTick()
    {
        var engine = new MarketDataEngine();
        KLine? closed = null;
        engine.OnKLineClosed += (_, k) => closed = k;

        var t0 = new DateTime(2024, 1, 2, 9, 0, 0);
        // Open=100, High=120, Low=90, Close=110, Volume=600
        engine.EnqueueTick(new TickData("X", 100m, 100, t0));
        engine.EnqueueTick(new TickData("X", 120m, 200, t0.AddSeconds(10)));
        engine.EnqueueTick(new TickData("X", 90m, 150, t0.AddSeconds(20)));
        engine.EnqueueTick(new TickData("X", 110m, 150, t0.AddSeconds(40)));
        engine.EnqueueTick(new TickData("X", 105m, 50, t0.AddMinutes(1))); // 觸發收盤

        var cts = new CancellationTokenSource(200);
        await engine.StartAsync(cts.Token);
        await Task.Delay(300);

        Assert.NotNull(closed);
        Assert.Equal(100m, closed!.Open);
        Assert.Equal(120m, closed.High);
        Assert.Equal(90m, closed.Low);
        Assert.Equal(110m, closed.Close);
        Assert.Equal(600L, closed.Volume);
    }
}
