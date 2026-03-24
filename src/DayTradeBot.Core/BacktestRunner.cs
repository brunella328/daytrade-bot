using DayTradeBot.Core.Broker;
using DayTradeBot.Core.Models;

namespace DayTradeBot.Core;

/// <summary>
/// 使用今日累積的原始 Tick 資料，同步回放策略邏輯，產生回測報告。
///
/// 流程：
/// 1. 依時間戳排序 Ticks
/// 2. 逐 Tick 更新 LocalRiskManager（TP/SL 即時觸發）
/// 3. 每根分鐘 K線收盤時送入 StrategyBrain 判斷進場
/// 4. 所有部位平倉後彙整損益
///
/// ⚠️ 使用獨立的 BacktestBrokerApi（無 Task.Delay），不影響正在運行的主系統。
/// </summary>
public class BacktestRunner
{
    private readonly TradingConfig _tradingConfig;

    public BacktestRunner(TradingConfig tradingConfig)
    {
        _tradingConfig = tradingConfig;
    }

    public BacktestResult Run(
        IReadOnlyList<RawTickEntry> ticks,
        StrategyConfig config,
        IReadOnlyDictionary<string, decimal> referencePrices)
    {
        var runAt  = DateTime.Now;
        var sorted = ticks.OrderBy(t => t.Timestamp).ToList();

        // 獨立的 broker（即時成交，不干擾主系統）
        var broker     = new BacktestBrokerApi();
        var indicators = new IndicatorEngine();
        var brain      = new StrategyBrain(broker, indicators);
        ApplyConfig(brain.Config, config);

        var riskMgr      = new LocalRiskManager(broker);
        var closedTrades = new List<BacktestTradeResult>();
        var redZoneLogs  = new List<string>();

        // 掛接事件
        brain.OnPositionOpened += (_, order) =>
            riskMgr.RegisterPosition(order.Symbol, order.FillPrice, order.Qty,
                                     order.TakeProfitPrice, order.StopLossPrice);

        riskMgr.OnPositionExited += (_, e) =>
        {
            var gross      = Math.Round((e.ExitPrice - e.EntryPrice) * e.Qty, 2);
            var commission = _tradingConfig.CalcCommission(e.EntryPrice, e.ExitPrice, e.Qty);
            var tax        = _tradingConfig.CalcTax(e.ExitPrice, e.Qty);
            closedTrades.Add(new BacktestTradeResult(
                e.Symbol, e.EntryPrice, e.ExitPrice, e.Qty,
                e.EntryTime, DateTime.Now, gross, commission, tax,
                Math.Round(gross - commission - tax, 2), e.Reason));
        };

        // Red Zone 攔截記錄（透過 Console 輸出擷取，或在此直接計算）
        // 直接在 tick 層做一次 snapshot：記錄跌幅超標的 (symbol, timestamp, dropPct)
        var redZoneHits = new List<BacktestRedZoneHit>();

        // 分鐘 K線建構（與 MarketDataEngine.ProcessTick 相同邏輯）
        var openCandles = new Dictionary<string, KLine>();

        foreach (var tick in sorted)
        {
            var tickData = new TickData(tick.Symbol, tick.Price, tick.Volume, tick.Timestamp);

            // TP/SL 即時檢查
            riskMgr.OnTick(null, tickData);

            var minuteStart = new DateTime(
                tick.Timestamp.Year, tick.Timestamp.Month, tick.Timestamp.Day,
                tick.Timestamp.Hour, tick.Timestamp.Minute, 0);

            if (openCandles.TryGetValue(tick.Symbol, out var candle))
            {
                if (minuteStart > candle.OpenTime)
                {
                    // K線收盤 → 送進策略
                    candle.CloseTime = candle.OpenTime.AddMinutes(1).AddSeconds(-1);

                    // 記錄 Red Zone 情況（StrategyBrain 內部會判斷，這裡只做記錄）
                    if (candle.PreviousClose.HasValue && candle.PreviousClose.Value > 0)
                    {
                        var drop = (double)(candle.Close - candle.PreviousClose.Value)
                                 / (double)candle.PreviousClose.Value;
                        if (drop <= -config.RedZoneThreshold)
                        {
                            redZoneHits.Add(new BacktestRedZoneHit(
                                candle.Symbol, candle.CloseTime,
                                candle.Close, candle.PreviousClose.Value,
                                Math.Round(drop * 100, 2)));
                        }
                    }

                    brain.OnKLineClosedAsync(null, candle).GetAwaiter().GetResult();
                    openCandles[tick.Symbol] = NewCandle(tick, minuteStart, referencePrices);
                }
                else
                {
                    candle.High   = Math.Max(candle.High, tick.Price);
                    candle.Low    = Math.Min(candle.Low,  tick.Price);
                    candle.Close  = tick.Price;
                    candle.Volume += tick.Volume;
                }
            }
            else
            {
                openCandles[tick.Symbol] = NewCandle(tick, minuteStart, referencePrices);
            }
        }

        // 結算剩餘未收盤的 K線
        foreach (var (_, candle) in openCandles)
        {
            candle.CloseTime = candle.OpenTime.AddMinutes(1).AddSeconds(-1);
            brain.OnKLineClosedAsync(null, candle).GetAwaiter().GetResult();
        }

        // 強制平倉仍持有的部位（以最後已知價格）
        foreach (var pos in riskMgr.GetPositions())
        {
            var lastPrice  = riskMgr.GetLatestPrice(pos.Symbol) ?? pos.FillPrice;
            var gross      = Math.Round((lastPrice - pos.FillPrice) * pos.Qty, 2);
            var commission = _tradingConfig.CalcCommission(pos.FillPrice, lastPrice, pos.Qty);
            var tax        = _tradingConfig.CalcTax(lastPrice, pos.Qty);
            closedTrades.Add(new BacktestTradeResult(
                pos.Symbol, pos.FillPrice, lastPrice, pos.Qty,
                pos.EntryTime, DateTime.Now, gross, commission, tax,
                Math.Round(gross - commission - tax, 2), "BacktestEnd"));
        }

        var totalNet  = Math.Round(closedTrades.Sum(t => t.NetPnL), 2);
        var wins      = closedTrades.Count(t => t.NetPnL > 0);
        var winRate   = closedTrades.Count > 0 ? Math.Round((double)wins / closedTrades.Count, 4) : 0d;
        var symbols   = sorted.Select(t => t.Symbol).Distinct().Order().ToList();

        return new BacktestResult(
            RunAt:          runAt,
            TotalTrades:    closedTrades.Count,
            WinTrades:      wins,
            WinRate:        winRate,
            TotalNetPnL:    totalNet,
            InitialCapital: _tradingConfig.InitialCapital,
            TickCount:      ticks.Count,
            Symbols:        symbols,
            Trades:         closedTrades,
            RedZoneHits:    redZoneHits
        );
    }

    // ── 輔助方法 ──────────────────────────────────────────────────────────

    private static KLine NewCandle(
        RawTickEntry tick,
        DateTime minuteStart,
        IReadOnlyDictionary<string, decimal> referencePrices) => new()
    {
        Symbol        = tick.Symbol,
        Open          = tick.Price,
        High          = tick.Price,
        Low           = tick.Price,
        Close         = tick.Price,
        Volume        = tick.Volume,
        OpenTime      = minuteStart,
        PreviousClose = referencePrices.TryGetValue(tick.Symbol, out var p) ? p : null
    };

    private static void ApplyConfig(StrategyConfig target, StrategyConfig source)
    {
        target.AdxThreshold      = source.AdxThreshold;
        target.RsiThreshold      = source.RsiThreshold;
        target.UseBbCondition    = source.UseBbCondition;
        target.TakeProfitPct     = source.TakeProfitPct;
        target.StopLossPct       = source.StopLossPct;
        target.EntryStartHour    = source.EntryStartHour;
        target.EntryEndHour      = source.EntryEndHour;
        target.ForceCloseHour    = source.ForceCloseHour;
        target.TotalCapital      = source.TotalCapital;
        target.MaxPositionPct    = source.MaxPositionPct;
        target.RedZoneThreshold  = source.RedZoneThreshold;
    }
}

// ── 回測專用 Broker（立即成交，無 Task.Delay）────────────────────────────

internal class BacktestBrokerApi : IBrokerApi
{
    public event EventHandler<TradeReport>? OnTradeReport;

    public Task<string> PlaceMarketBuyAsync(string symbol, long qty)
    {
        var orderId = Guid.NewGuid().ToString("N")[..8];
        // 立即觸發成交回報（FillPrice=0，StrategyBrain 從 _pendingFillPrice 取K線收盤價）
        OnTradeReport?.Invoke(this, new TradeReport(orderId, symbol, 0m, qty, DateTime.Now));
        return Task.FromResult(orderId);
    }

    public Task<string> PlaceMarketSellAsync(string symbol, long qty)
        => Task.FromResult("SELL_" + Guid.NewGuid().ToString("N")[..8]);

    public Task<OcoOrderResult> PlaceOcoOrderAsync(string symbol, long qty, decimal tp, decimal sl)
        => Task.FromResult(new OcoOrderResult($"TP_{symbol}", $"SL_{symbol}"));

    public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
}

// ── 資料模型 ──────────────────────────────────────────────────────────────

public record BacktestTradeResult(
    string   Symbol,
    decimal  EntryPrice,
    decimal  ExitPrice,
    long     Qty,
    DateTime EntryTime,
    DateTime ExitTime,
    decimal  GrossPnL,
    decimal  Commission,
    decimal  Tax,
    decimal  NetPnL,
    string   ExitReason
);

public record BacktestRedZoneHit(
    string   Symbol,
    DateTime Time,
    decimal  Close,
    decimal  PreviousClose,
    double   DropPct   // 負值，如 -7.23 表示跌幅 7.23%
);

public record BacktestResult(
    DateTime                        RunAt,
    int                             TotalTrades,
    int                             WinTrades,
    double                          WinRate,
    decimal                         TotalNetPnL,
    decimal                         InitialCapital,
    int                             TickCount,
    IReadOnlyList<string>           Symbols,
    IReadOnlyList<BacktestTradeResult> Trades,
    IReadOnlyList<BacktestRedZoneHit>  RedZoneHits
);
