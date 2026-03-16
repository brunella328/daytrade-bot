using DayTradeBot.Core;
using DayTradeBot.Core.Models;

namespace DayTradeBot.Api;

/// <summary>
/// Dry Run 用的虛擬 Tick 產生器。
/// 模擬數檔股票的隨機 Tick，以測試整個資料流。
/// </summary>
public class MockTickProducer : BackgroundService
{
    private readonly MarketDataEngine _engine;
    private readonly ILogger<MockTickProducer> _logger;

    private static readonly string[] Symbols = ["2330", "2317", "2454", "2382", "3008"];
    private readonly Dictionary<string, decimal> _prices = new()
    {
        ["2330"] = 780m,
        ["2317"] = 105m,
        ["2454"] = 680m,
        ["2382"] = 220m,
        ["3008"] = 550m
    };

    private readonly Random _rng = new();

    public MockTickProducer(MarketDataEngine engine, ILogger<MockTickProducer> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[MockTickProducer] 開始產生虛擬 Tick");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            // 只在交易時段產生 Tick
            if (now.TimeOfDay >= TimeSpan.FromHours(9) && now.TimeOfDay <= TimeSpan.FromHours(13.5))
            {
                foreach (var symbol in Symbols)
                {
                    // 隨機小幅度價格變動（±0.5%）
                    var change = (decimal)(_rng.NextDouble() * 0.01 - 0.005);
                    _prices[symbol] = Math.Round(_prices[symbol] * (1 + change), 2);

                    var tick = new TickData(symbol, _prices[symbol], _rng.Next(100, 1000), now);
                    _engine.EnqueueTick(tick);
                }
            }

            await Task.Delay(500, stoppingToken); // 每 0.5 秒一波 Tick
        }
    }
}
