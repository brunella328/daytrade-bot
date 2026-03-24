using DayTradeBot.Core.Broker;
using DayTradeBot.Core.Models;

namespace DayTradeBot.Core;

public record OcoOrder(
    string Symbol,
    string EntryOrderId,
    decimal FillPrice,
    decimal TakeProfitPrice,
    decimal StopLossPrice,
    long Qty,
    DateTime EntryTime
);

/// <summary>
/// 核心交易邏輯：
/// - 時間濾網：09:00–13:00 允許進場，13:00–13:30 強制平倉
/// - Triple Confirmation：ADX&lt;25 AND Close&lt;BBLower AND RSI&lt;30
/// - 單一部位，允許同股票重複進場（前一筆 OCO 完成後才能再進場）
/// </summary>
public class StrategyBrain
{
    private readonly IBrokerApi _broker;
    private readonly IndicatorEngine _indicators;
    private readonly Dictionary<string, List<KLine>> _klineHistory = new();
    private readonly Dictionary<string, OcoOrder> _openPositions = new();
    private bool _forceCloseSent = false;
    private readonly Dictionary<string, double?> _prevRsi = new();

    public StrategyConfig Config { get; } = new();

    /// <summary>今日大盤（TWII）漲跌幅，由外部注入（DryRun 模式預設 0）</summary>
    public decimal TwiiDropPct { get; set; } = 0m;

    public event EventHandler<OcoOrder>? OnPositionOpened;
    public event EventHandler<(OcoOrder Order, decimal ExitPrice, string Reason)>? OnPositionClosed;

    public StrategyBrain(IBrokerApi broker, IndicatorEngine indicators)
    {
        _broker = broker;
        _indicators = indicators;
        _broker.OnTradeReport += HandleTradeReport;
    }

    public async Task OnKLineClosedAsync(object? sender, KLine kline)
    {
        var now = kline.CloseTime == default ? DateTime.Now : kline.CloseTime;

        // 強制清倉期
        if (now.TimeOfDay >= TimeSpan.FromHours(Config.EntryEndHour) &&
            now.TimeOfDay < TimeSpan.FromHours(Config.ForceCloseHour))
        {
            if (_forceCloseSent) return;
            _forceCloseSent = true;
            await ForceCloseAllAsync();
            return;
        }

        // 允許進場時段
        if (now.TimeOfDay < TimeSpan.FromHours(Config.EntryStartHour) ||
            now.TimeOfDay >= TimeSpan.FromHours(Config.EntryEndHour))
        {
            if (now.TimeOfDay < TimeSpan.FromHours(Config.EntryStartHour) && _forceCloseSent)
                _forceCloseSent = false;
            return;
        }

        // 大盤方向濾網
        if (TwiiDropPct < Config.MarketDropThreshold)
        {
            Console.WriteLine($"[MARKET GUARD] 大盤跌幅 {TwiiDropPct:P2}，停止進場");
            return;
        }

        // 已有部位 → 不重複進場同標的
        if (_openPositions.ContainsKey(kline.Symbol)) return;

        // 全域持倉上限
        if (_openPositions.Count >= Config.MaxConcurrentPositions) return;

        // ── 深水區禁區 (Red Zone Guard) ─────────────────────────────────────
        // 當前跌幅超過門檻時，不進場（防止跌停鎖死停損單）
        if (kline.PreviousClose.HasValue && kline.PreviousClose.Value > 0)
        {
            var dropPct = (double)(kline.Close - kline.PreviousClose.Value) / (double)kline.PreviousClose.Value;
            if (dropPct <= -Config.RedZoneThreshold)
            {
                Console.WriteLine($"[RED ZONE] {kline.Symbol} 跌幅={dropPct:P2}，超過 {Config.RedZoneThreshold:P0} 門檻，禁止進場");
                return;
            }
        }

        // 累積 K線歷史
        if (!_klineHistory.TryGetValue(kline.Symbol, out var history))
        {
            history = new List<KLine>();
            _klineHistory[kline.Symbol] = history;
        }
        history.Add(kline);

        // 計算指標
        var result = _indicators.Calculate(history);
        if (result is null) return;

        // Triple Confirmation
        bool adxOk = result.Adx.HasValue && result.Adx.Value < Config.AdxThreshold;
        bool bbOk  = !Config.UseBbCondition ||
                     (result.BbLower.HasValue && (double)kline.Close < result.BbLower.Value);
        bool rsiThisBar = result.Rsi.HasValue && result.Rsi.Value < Config.RsiThreshold;
        _prevRsi.TryGetValue(kline.Symbol, out var prevRsiVal);
        bool rsiOk = rsiThisBar && prevRsiVal.HasValue && prevRsiVal.Value < Config.RsiThreshold;
        _prevRsi[kline.Symbol] = result.Rsi;

        _lastIndicators[kline.Symbol] = new IndicatorSnapshot(
            kline.Symbol, kline.Close, kline.CloseTime,
            result.Adx, result.BbLower, result.Rsi,
            adxOk, bbOk, rsiOk);

        if (adxOk && bbOk && rsiOk)
        {
            // 部位規模：計算張數，不足 1 張則跳過
            var lots = Config.CalcLots(kline.Close);
            if (lots < 1)
            {
                Console.WriteLine($"[SKIP] {kline.Symbol} 價格={kline.Close} 資金不足 1 張（需 {kline.Close * 1000:N0} 元）");
                return;
            }
            var qtyShares = lots * 1000; // 轉換為股數

            Console.WriteLine($"[SIGNAL] {kline.Symbol} ADX={result.Adx:F1} BB={result.BbLower:F2} RSI={result.Rsi:F1} Close={kline.Close} 買進{lots}張({qtyShares}股)");
            _pendingFillPrice[kline.Symbol] = kline.Close;
            if (kline.PreviousClose.HasValue)
                _pendingPreviousClose[kline.Symbol] = kline.PreviousClose.Value;
            await _broker.PlaceMarketBuyAsync(kline.Symbol, qtyShares);
        }
    }

    // Mock 環境用：暫存預期成交價 & 前收盤
    private readonly Dictionary<string, decimal> _pendingFillPrice = new();
    private readonly Dictionary<string, decimal> _pendingPreviousClose = new();

    // 最新指標快照（供 debug 查詢）
    private readonly Dictionary<string, IndicatorSnapshot> _lastIndicators = new();
    public IReadOnlyDictionary<string, IndicatorSnapshot> LastIndicators => _lastIndicators;

    private void HandleTradeReport(object? sender, TradeReport report)
    {
        // Mock 環境：FillPrice = 0，從暫存取 K線收盤價
        var fillPrice = report.FillPrice == 0
            ? (_pendingFillPrice.TryGetValue(report.Symbol, out var p) ? p : report.FillPrice)
            : report.FillPrice;

        _pendingFillPrice.Remove(report.Symbol);

        // 計算原始 TP/SL
        var tp = CalculateTakeProfit(fillPrice);
        var sl = CalculateStopLoss(fillPrice);

        // 套用 Tick Size rounding
        tp = TickSizeHelper.RoundToTickSize(tp);
        sl = TickSizeHelper.RoundToTickSize(sl);

        // 漲跌停 guard（有 PreviousClose 時才檢查）
        if (_pendingPreviousClose.TryGetValue(report.Symbol, out var prevClose) && prevClose > 0)
        {
            var upperLimit = TickSizeHelper.UpperLimit(prevClose);
            var lowerLimit = TickSizeHelper.LowerLimit(prevClose);
            tp = Math.Min(tp, upperLimit);
            sl = Math.Max(sl, lowerLimit);
            _pendingPreviousClose.Remove(report.Symbol);
        }

        var oco = new OcoOrder(report.Symbol, report.OrderId, fillPrice, tp, sl, report.Qty, report.FilledAt);
        _openPositions[report.Symbol] = oco;
        OnPositionOpened?.Invoke(this, oco);

        Console.WriteLine($"[ENTRY] {report.Symbol} fill={fillPrice:F2} TP={tp:F2} SL={sl:F2}");

        // 送出 OCO（fire-and-forget，不 await）
        _ = _broker.PlaceOcoOrderAsync(report.Symbol, report.Qty, tp, sl);
    }

    /// <summary>由 MockBrokerApi 或真實 OCO 回報呼叫，通知部位已出場</summary>
    public void NotifyPositionClosed(string symbol, decimal exitPrice, string reason)
    {
        if (!_openPositions.TryGetValue(symbol, out var order)) return;
        _openPositions.Remove(symbol);
        OnPositionClosed?.Invoke(this, (order, exitPrice, reason));
        Console.WriteLine($"[EXIT] {symbol} {reason} @ {exitPrice:F2} PnL={exitPrice - order.FillPrice:F2}");
    }

    private async Task ForceCloseAllAsync()
    {
        foreach (var (symbol, order) in _openPositions.ToList())
        {
            await _broker.CancelOrderAsync(order.EntryOrderId);
            // 以當前市價出場（Mock 用進場價模擬）
            NotifyPositionClosed(symbol, order.FillPrice, "ForceClose");
        }
    }

    public decimal CalculateTakeProfit(decimal fillPrice) => Math.Round(fillPrice * (1 + Config.TakeProfitPct), 2);
    public decimal CalculateStopLoss(decimal fillPrice)   => Math.Round(fillPrice * (1 - Config.StopLossPct),   2);
}

public record IndicatorSnapshot(
    string   Symbol,
    decimal  Close,
    DateTime Time,
    double?  Adx,
    double?  BbLower,
    double?  Rsi,
    bool     AdxOk,
    bool     BbOk,
    bool     RsiOk
);
