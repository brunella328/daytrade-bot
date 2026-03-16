using DayTradeBot.Core;
using Xunit;

namespace DayTradeBot.Tests;

public class OcoPriceTests
{
    [Theory]
    [InlineData(100.00, 100.50)]
    [InlineData(780.00, 783.90)]
    [InlineData(105.50, 106.03)]
    [InlineData(1000.00, 1005.00)]
    public void TakeProfit_IsCorrect(decimal fillPrice, decimal expected)
    {
        var tp = StrategyBrain.CalculateTakeProfit(fillPrice);
        Assert.Equal(expected, tp);
    }

    [Theory]
    [InlineData(100.00, 99.00)]
    [InlineData(780.00, 772.20)]
    [InlineData(105.50, 104.45)]
    [InlineData(1000.00, 990.00)]
    public void StopLoss_IsCorrect(decimal fillPrice, decimal expected)
    {
        var sl = StrategyBrain.CalculateStopLoss(fillPrice);
        Assert.Equal(expected, sl);
    }

    [Fact]
    public void TakeProfit_AlwaysGreaterThanFill()
    {
        foreach (var price in new[] { 10m, 100m, 500m, 1200m })
            Assert.True(StrategyBrain.CalculateTakeProfit(price) > price);
    }

    [Fact]
    public void StopLoss_AlwaysLessThanFill()
    {
        foreach (var price in new[] { 10m, 100m, 500m, 1200m })
            Assert.True(StrategyBrain.CalculateStopLoss(price) < price);
    }
}
