using DayTradeBot.Core;
using DayTradeBot.Core.Broker;
using DayTradeBot.Storage;

namespace DayTradeBot.Api;

/// <summary>
/// 協調 MarketDataEngine → IndicatorEngine → StrategyBrain → TradeRepository 的整合服務。
/// </summary>
public class TradingEngine : BackgroundService
{
    private readonly MarketDataEngine _market;
    private readonly StrategyBrain _brain;
    private readonly TradeRepository _repo;
    private readonly ILogger<TradingEngine> _logger;

    public TradingEngine(
        MarketDataEngine market,
        StrategyBrain brain,
        TradeRepository repo,
        ILogger<TradingEngine> logger)
    {
        _market = market;
        _brain = brain;
        _repo = repo;
        _logger = logger;

        // 訂閱 K線收盤事件
        _market.OnKLineClosed += async (s, kline) =>
            await _brain.OnKLineClosedAsync(s, kline);

        // 訂閱出場事件 → 寫入 SQLite
        _brain.OnPositionClosed += async (s, e) =>
        {
            var (order, exitPrice, reason) = e;
            var record = new TradeRecord
            {
                Symbol = order.Symbol,
                EntryPrice = order.FillPrice,
                ExitPrice = exitPrice,
                Qty = order.Qty,
                EntryTime = order.EntryTime,
                ExitTime = DateTime.Now,
                PnL = Math.Round((exitPrice - order.FillPrice) * order.Qty, 2),
                ExitReason = reason
            };
            await _repo.InsertTradeAsync(record);
            _logger.LogInformation("[TRADE SAVED] {Symbol} {Reason} PnL={PnL}", record.Symbol, record.ExitReason, record.PnL);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[TradingEngine] 啟動，模式：Dry Run");
        await _market.StartAsync(stoppingToken);
    }
}
