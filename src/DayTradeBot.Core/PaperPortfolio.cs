namespace DayTradeBot.Core;

/// <summary>
/// 模擬交易帳戶，追蹤本金、現金、持倉市值與已實現損益。
/// 掛接 StrategyBrain.OnPositionOpened 與 LocalRiskManager.OnPositionExited 事件。
/// </summary>
public class PaperPortfolio
{
    private readonly TradingConfig _config;
    private readonly LocalRiskManager _riskMgr;
    private readonly object _lock = new();

    private decimal _cash;
    private decimal _realizedPnL;
    private int _totalTrades;
    private int _winTrades;

    public PaperPortfolio(TradingConfig config, LocalRiskManager riskMgr)
    {
        _config  = config;
        _riskMgr = riskMgr;
        _cash    = config.InitialCapital;
    }

    // ── 事件掛接 ──────────────────────────────────────────────────────────

    public void OnPositionOpened(object? sender, OcoOrder order)
    {
        lock (_lock)
        {
            var cost = order.FillPrice * order.Qty;
            _cash -= cost;
        }
    }

    public void OnPositionExited(object? sender, PositionExitArgs e)
    {
        lock (_lock)
        {
            var proceeds   = e.ExitPrice * e.Qty;
            var commission = _config.CalcCommission(e.EntryPrice, e.ExitPrice, e.Qty);
            var tax        = _config.CalcTax(e.ExitPrice, e.Qty);
            var net        = Math.Round((e.ExitPrice - e.EntryPrice) * e.Qty - commission - tax, 2);

            _cash         += proceeds;
            _realizedPnL  += net;
            _totalTrades++;
            if (net > 0) _winTrades++;
        }
    }

    // ── 重設本金 ──────────────────────────────────────────────────────────

    public void Reset(decimal newCapital)
    {
        lock (_lock)
        {
            _config.InitialCapital = newCapital;
            _cash         = newCapital;
            _realizedPnL  = 0;
            _totalTrades  = 0;
            _winTrades    = 0;
        }
    }

    // ── 快照查詢 ──────────────────────────────────────────────────────────

    public PortfolioSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var positions   = _riskMgr.GetPositions();
            var prices      = _riskMgr.GetLatestPrices();

            var openPositions = positions.Select(p =>
            {
                var latest     = prices.TryGetValue(p.Symbol, out var lp) ? lp : p.FillPrice;
                var cost       = p.FillPrice * p.Qty;
                var mktValue   = latest * p.Qty;
                var unrealized = Math.Round(mktValue - cost, 2);
                return new OpenPositionView(
                    p.Symbol, p.FillPrice, p.Qty,
                    latest, mktValue, unrealized,
                    p.TakeProfitPrice, p.StopLossPrice, p.EntryTime);
            }).ToList();

            var positionValue = openPositions.Sum(p => p.MarketValue);
            var unrealizedPnL = openPositions.Sum(p => p.UnrealizedPnL);
            var totalValue    = _cash + positionValue;

            return new PortfolioSnapshot(
                InitialCapital: _config.InitialCapital,
                Cash:           Math.Round(_cash, 2),
                PositionValue:  Math.Round(positionValue, 2),
                TotalValue:     Math.Round(totalValue, 2),
                RealizedPnL:    Math.Round(_realizedPnL, 2),
                UnrealizedPnL:  Math.Round(unrealizedPnL, 2),
                TotalPnL:       Math.Round(_realizedPnL + unrealizedPnL, 2),
                TotalTrades:    _totalTrades,
                WinRate:        _totalTrades > 0 ? Math.Round((double)_winTrades / _totalTrades, 4) : 0,
                OpenPositions:  openPositions
            );
        }
    }
}

public record PortfolioSnapshot(
    decimal InitialCapital,
    decimal Cash,
    decimal PositionValue,
    decimal TotalValue,
    decimal RealizedPnL,
    decimal UnrealizedPnL,
    decimal TotalPnL,
    int     TotalTrades,
    double  WinRate,
    IReadOnlyList<OpenPositionView> OpenPositions
);

public record OpenPositionView(
    string  Symbol,
    decimal EntryPrice,
    long    Qty,
    decimal LatestPrice,
    decimal MarketValue,
    decimal UnrealizedPnL,
    decimal TakeProfitPrice,
    decimal StopLossPrice,
    DateTime EntryTime
);
