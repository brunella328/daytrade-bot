using DayTradeBot.Core;
using DayTradeBot.Storage;

namespace DayTradeBot.Api;

public class TradingEngine : BackgroundService
{
    private readonly MarketDataEngine _market;
    private readonly StrategyBrain _brain;
    private readonly TradeRepository _repo;
    private readonly TradingConfig _config;
    private readonly ILogger<TradingEngine> _logger;

    public TradingEngine(
        MarketDataEngine market,
        StrategyBrain brain,
        TradeRepository repo,
        TradingConfig config,
        ILogger<TradingEngine> logger)
    {
        _market = market;
        _brain = brain;
        _repo = repo;
        _config = config;
        _logger = logger;

        _market.OnKLineClosed += async (s, kline) =>
            await _brain.OnKLineClosedAsync(s, kline);

        _brain.OnPositionClosed += async (s, e) =>
        {
            var (order, exitPrice, reason) = e;

            var grossPnl = Math.Round((exitPrice - order.FillPrice) * order.Qty, 2);
            // 手續費：買入 + 賣出各一次
            var commission = Math.Round(
                (order.FillPrice * order.Qty * _config.CommissionRate) +
                (exitPrice * order.Qty * _config.CommissionRate), 2);
            // 交易稅：賣出側
            var tax = Math.Round(exitPrice * order.Qty * _config.TaxRate, 2);
            var netPnl = Math.Round(grossPnl - commission - tax, 2);

            var record = new TradeRecord
            {
                Symbol = order.Symbol,
                EntryPrice = order.FillPrice,
                ExitPrice = exitPrice,
                Qty = order.Qty,
                EntryTime = order.EntryTime,
                ExitTime = DateTime.Now,
                GrossPnL = grossPnl,
                Commission = commission,
                Tax = tax,
                NetPnL = netPnl,
                ExitReason = reason
            };
            await _repo.InsertTradeAsync(record);
            _logger.LogInformation(
                "[TRADE] {Symbol} {Reason} gross={Gross} comm={Comm} tax={Tax} net={Net}",
                record.Symbol, record.ExitReason, grossPnl, commission, tax, netPnl);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_config.IsLive)
        {
            _logger.LogWarning("⚠️  LIVE MODE — 真實下單已啟用，請確認帳戶餘額與風險設定");
            Console.WriteLine("⚠️  ====================================");
            Console.WriteLine("⚠️  LIVE MODE：真實下單已啟用");
            Console.WriteLine("⚠️  ====================================");
        }
        else
        {
            _logger.LogInformation("[TradingEngine] 啟動，模式：Dry Run（模擬交易）");
        }

        await _market.StartAsync(stoppingToken);
    }
}
