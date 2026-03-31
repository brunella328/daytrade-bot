using DayTradeBot.Core;
using DayTradeBot.Storage;

namespace DayTradeBot.Api;

public class TradingEngine : BackgroundService
{
    private readonly MarketDataEngine _market;
    private readonly StrategyBrain _brain;
    private readonly LocalRiskManager _riskMgr;
    private readonly TradeRepository _repo;
    private readonly TradingConfig _config;
    private readonly ILogger<TradingEngine> _logger;

    public TradingEngine(
        MarketDataEngine market,
        StrategyBrain brain,
        LocalRiskManager riskMgr,
        TradeRepository repo,
        TradingConfig config,
        ILogger<TradingEngine> logger)
    {
        _market = market;
        _brain = brain;
        _riskMgr = riskMgr;
        _repo = repo;
        _config = config;
        _logger = logger;

        // K線收盤 → StrategyBrain
        _market.OnKLineClosed += async (s, kline) =>
            await _brain.OnKLineClosedAsync(s, kline);

        // Tick 更新 → LocalRiskManager 即時檢核止盈止損
        _market.OnTickEnqueued += _riskMgr.OnTick;

        // 進場成交 → LocalRiskManager 登記部位
        _brain.OnPositionOpened += (_, order) =>
        {
            _riskMgr.RegisterPosition(
                order.Symbol,
                order.FillPrice,
                order.Qty,
                order.TakeProfitPrice,
                order.StopLossPrice);
        };

        // LocalRiskManager 出場 → 寫 DB
        _riskMgr.OnPositionExited += async (_, e) =>
        {
            var gross      = Math.Round((e.ExitPrice - e.EntryPrice) * e.Qty, 2);
            var commission = _config.CalcCommission(e.EntryPrice, e.ExitPrice, e.Qty);
            var tax        = _config.CalcTax(e.ExitPrice, e.Qty);
            var netPnl     = Math.Round(gross - commission - tax, 2);

            await _repo.InsertTradeAsync(new TradeRecord
            {
                Symbol = e.Symbol,
                EntryPrice = e.EntryPrice,
                ExitPrice = e.ExitPrice,
                Qty = e.Qty,
                EntryTime = e.EntryTime,
                ExitTime = DateTime.Now,
                GrossPnL = gross,
                Commission = commission,
                Tax = tax,
                NetPnL = netPnl,
                ExitReason = e.Reason
            });
            _logger.LogInformation("[TRADE] {Symbol} {Reason} net={Net}", e.Symbol, e.Reason, netPnl);
            _brain.NotifyPositionClosed(e.Symbol, e.ExitPrice, e.Reason);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_config.IsLive)
        {
            _logger.LogWarning("⚠️  LIVE MODE — 真實下單已啟用");
            Console.WriteLine("⚠️  ====================================");
            Console.WriteLine("⚠️  LIVE MODE：真實下單已啟用");
            Console.WriteLine("⚠️  ====================================");
        }
        else
        {
            _logger.LogInformation("[TradingEngine] 啟動，模式：Dry Run");
        }

        await _market.StartAsync(stoppingToken);
    }
}
