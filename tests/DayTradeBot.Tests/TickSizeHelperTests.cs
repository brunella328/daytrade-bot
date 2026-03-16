using DayTradeBot.Core;
using Xunit;

namespace DayTradeBot.Tests;

public class TickSizeHelperTests
{
    [Theory]
    [InlineData(5.0,    0.01)]
    [InlineData(9.99,   0.01)]
    [InlineData(10.0,   0.05)]
    [InlineData(49.9,   0.05)]
    [InlineData(50.0,   0.10)]
    [InlineData(99.9,   0.10)]
    [InlineData(100.0,  0.50)]
    [InlineData(499.0,  0.50)]
    [InlineData(500.0,  1.00)]
    [InlineData(999.0,  1.00)]
    [InlineData(1000.0, 5.00)]
    [InlineData(1500.0, 5.00)]
    public void GetTickSize_CorrectByRange(double price, double expected)
    {
        Assert.Equal((decimal)expected, TickSizeHelper.GetTickSize((decimal)price));
    }

    [Theory]
    [InlineData(95.43,  95.40)]   // tick=0.1
    [InlineData(95.45,  95.50)]   // tick=0.1
    [InlineData(102.75, 103.00)]  // tick=0.5
    [InlineData(780.3,  780.00)]  // tick=1
    [InlineData(780.5,  781.00)]  // tick=1
    public void RoundToTickSize_Correct(double price, double expected)
    {
        Assert.Equal((decimal)expected, TickSizeHelper.RoundToTickSize((decimal)price));
    }

    [Fact]
    public void UpperLimit_CorrectFor100Price()
    {
        // 前收 100 → 漲停 = 110，tick=0.5 → floor(110/0.5)*0.5 = 110
        Assert.Equal(110.0m, TickSizeHelper.UpperLimit(100m));
    }

    [Fact]
    public void LowerLimit_CorrectFor100Price()
    {
        // 前收 100 → 跌停 = 90，tick=0.1 → ceiling(90/0.1)*0.1 = 90
        Assert.Equal(90.0m, TickSizeHelper.LowerLimit(100m));
    }

    [Fact]
    public void UpperLimit_AlwaysGreaterThanLowerLimit()
    {
        foreach (var price in new[] { 10m, 50m, 100m, 500m, 1000m })
            Assert.True(TickSizeHelper.UpperLimit(price) > TickSizeHelper.LowerLimit(price));
    }
}
