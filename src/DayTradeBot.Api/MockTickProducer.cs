using DayTradeBot.Core;
using DayTradeBot.Core.Models;

namespace DayTradeBot.Api;

/// <summary>
/// Dry Run 用的虛擬 Tick 產生器。
/// 正常時段：隨機小幅波動。
/// 每 5 分鐘隨機挑一檔觸發「急跌模式」（連續下跌數根 K 線），
/// 使 RSI 和布林通道條件可以被觸發，模擬真實市場超賣情境。
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

    // 急跌模式：記錄每檔剩餘急跌 tick 數
    private readonly Dictionary<string, int> _dropTicks = new();
    private readonly Random _rng = new();
    private DateTime _nextDropTrigger = DateTime.Now.AddMinutes(2); // 2分鐘後第一次觸發

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

            if (now.TimeOfDay >= TimeSpan.FromHours(9) && now.TimeOfDay <= TimeSpan.FromHours(13.5))
            {
                // 每 5 分鐘觸發一次急跌
                if (now >= _nextDropTrigger)
                {
                    var target = Symbols[_rng.Next(Symbols.Length)];
                    _dropTicks[target] = 20; // 連續 20 個 tick（約 10 秒）急跌
                    _logger.LogInformation("[MockTickProducer] 急跌觸發：{Symbol}", target);
                    _nextDropTrigger = now.AddMinutes(5);
                }

                foreach (var symbol in Symbols)
                {
                    decimal change;
                    if (_dropTicks.TryGetValue(symbol, out var remaining) && remaining > 0)
                    {
                        // 急跌：每 tick 下跌 0.3–0.6%
                        change = -(decimal)(_rng.NextDouble() * 0.003 + 0.003);
                        _dropTicks[symbol] = remaining - 1;
                    }
                    else
                    {
                        // 正常：±0.3% 小幅波動（比之前保守一點，讓 BB 帶寬更窄）
                        change = (decimal)(_rng.NextDouble() * 0.006 - 0.003);
                    }

                    _prices[symbol] = Math.Round(_prices[symbol] * (1 + change), 2);
                    _engine.EnqueueTick(new TickData(symbol, _prices[symbol], _rng.Next(100, 1000), now));
                }
            }

            await Task.Delay(500, stoppingToken);
        }
    }
}
