using DayTradeBot.Core;
using DayTradeBot.Core.Broker;
using DayTradeBot.Core.Models;
using Xunit;

namespace DayTradeBot.Tests;

public class StrategyBrainTests
{
    [Theory]
    [InlineData(24.9, 99.0, 95.0, 29.9, true)]   // 全中
    [InlineData(25.1, 99.0, 95.0, 29.9, false)]   // ADX 不符
    [InlineData(24.9, 99.0, 101.0, 29.9, false)]  // Close > BBLower
    [InlineData(24.9, 99.0, 95.0, 30.1, false)]   // RSI 不符
    public async Task TripleConfirmation_CorrectlyTriggered(
        double adx, double bbLower, double closePrice, double rsi, bool shouldTrigger)
    {
        var mockBroker = new MockBrokerApi();
        var mockIndicator = new MockIndicatorEngine(adx, bbLower, rsi);
        var brain = new TestableStrategyBrain(mockBroker, mockIndicator);

        var signalFired = false;
        brain.OnPositionOpened += (_, _) => signalFired = true;

        var kline = new KLine
        {
            Symbol = "2330",
            Open = (decimal)closePrice,
            High = (decimal)closePrice + 1,
            Low = (decimal)closePrice - 1,
            Close = (decimal)closePrice,
            Volume = 500,
            OpenTime = new DateTime(2024, 1, 2, 10, 0, 0),
            CloseTime = new DateTime(2024, 1, 2, 10, 0, 59)
        };

        await brain.OnKLineClosedAsync(null, kline);
        await Task.Delay(200);

        Assert.Equal(shouldTrigger, signalFired);
    }

    [Fact]
    public async Task TimeFilter_BlocksEntryAfter1300()
    {
        var mockBroker = new MockBrokerApi();
        var mockIndicator = new MockIndicatorEngine(24.0, 99.0, 29.0); // 全部通過
        var brain = new TestableStrategyBrain(mockBroker, mockIndicator);

        var signalFired = false;
        brain.OnPositionOpened += (_, _) => signalFired = true;

        var kline = new KLine
        {
            Symbol = "2330",
            Open = 95m, High = 96m, Low = 94m, Close = 95m, Volume = 500,
            OpenTime = new DateTime(2024, 1, 2, 13, 0, 0),
            CloseTime = new DateTime(2024, 1, 2, 13, 0, 59) // 13:00 → 禁止進場
        };

        await brain.OnKLineClosedAsync(null, kline);
        await Task.Delay(200);

        Assert.False(signalFired);
    }
}

// ── Test Doubles ────────────────────────────────────────

/// <summary>可注入固定指標值的 IndicatorEngine 替代品</summary>
public class MockIndicatorEngine : IndicatorEngine
{
    private readonly double _adx, _bbLower, _rsi;
    public MockIndicatorEngine(double adx, double bbLower, double rsi)
        => (_adx, _bbLower, _rsi) = (adx, bbLower, rsi);

    public new IndicatorResult? Calculate(IReadOnlyList<KLine> _)
        => new(_adx, _bbLower, _rsi);
}

/// <summary>注入 MockIndicatorEngine 的 StrategyBrain</summary>
public class TestableStrategyBrain : StrategyBrain
{
    public TestableStrategyBrain(IBrokerApi broker, MockIndicatorEngine indicators)
        : base(broker, indicators) { }
}
