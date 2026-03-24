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
    [InlineData(100.00, 100.80)]   // 100.00 * 1.008 = 100.800 → 100.80
    [InlineData(780.00, 786.24)]   // 780.00 * 1.008 = 786.240 → 786.24
    [InlineData(105.50, 106.34)]   // 105.50 * 1.008 = 106.344 → 106.34
    [InlineData(1000.00, 1008.00)] // 1000.00 * 1.008 = 1008.000 → 1008.00
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
