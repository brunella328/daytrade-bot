using DayTradeBot.Core;
using DayTradeBot.Core.Broker;
using Xunit;

namespace DayTradeBot.Tests;

public class OcoPriceTests
{
    // CalculateTakeProfit / CalculateStopLoss 是 instance methods，依賴 Config.TakeProfitPct / StopLossPct。
    // 使用 MockBrokerApi + 真實 IndicatorEngine 建立 StrategyBrain instance 後呼叫。
    private static StrategyBrain CreateBrain() =>
        new StrategyBrain(new MockBrokerApi(), new IndicatorEngine());

    [Theory]
    [InlineData(100.00, 101.30)]   // 100.00 * 1.013 = 101.300 → 101.30
    [InlineData(780.00, 790.14)]   // 780.00 * 1.013 = 790.140 → 790.14
    [InlineData(105.50, 106.87)]   // 105.50 * 1.013 = 106.8715 → 106.87
    [InlineData(1000.00, 1013.00)] // 1000.00 * 1.013 = 1013.000 → 1013.00
    public void TakeProfit_IsCorrect(decimal fillPrice, decimal expected)
    {
        var brain = CreateBrain();
        var tp = brain.CalculateTakeProfit(fillPrice);
        Assert.Equal(expected, tp);
    }

    [Theory]
    [InlineData(100.00, 99.00)]
    [InlineData(780.00, 772.20)]
    [InlineData(105.50, 104.44)]  // 105.5×0.990=104.445 → banker's rounding → 104.44
    [InlineData(1000.00, 990.00)]
    public void StopLoss_IsCorrect(decimal fillPrice, decimal expected)
    {
        var brain = CreateBrain();
        var sl = brain.CalculateStopLoss(fillPrice);
        Assert.Equal(expected, sl);
    }

    [Fact]
    public void TakeProfit_AlwaysGreaterThanFill()
    {
        var brain = CreateBrain();
        foreach (var price in new[] { 10m, 100m, 500m, 1200m })
            Assert.True(brain.CalculateTakeProfit(price) > price);
    }

    [Fact]
    public void StopLoss_AlwaysLessThanFill()
    {
        var brain = CreateBrain();
        foreach (var price in new[] { 10m, 100m, 500m, 1200m })
            Assert.True(brain.CalculateStopLoss(price) < price);
    }
}
