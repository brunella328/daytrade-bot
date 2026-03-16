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

    // 單筆下單固定張數（Phase 1 固定 1 張）
    private const long DefaultQty = 1;

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

        // 強制清倉期：13:00–13:30
        if (now.TimeOfDay >= TimeSpan.FromHours(13) && now.TimeOfDay < TimeSpan.FromHours(13.5))
        {
            await ForceCloseAllAsync();
            return;
        }

        // 允許進場時段：09:00–13:00
        if (now.TimeOfDay < TimeSpan.FromHours(9) || now.TimeOfDay >= TimeSpan.FromHours(13))
            return;

        // 已有部位 → 不重複進場同標的
        if (_openPositions.ContainsKey(kline.Symbol)) return;

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
        bool adxOk = result.Adx.HasValue && result.Adx.Value < 25;
        bool bbOk = result.BbLower.HasValue && (double)kline.Close < result.BbLower.Value;
        bool rsiOk = result.Rsi.HasValue && result.Rsi.Value < 30;

        if (adxOk && bbOk && rsiOk)
        {
            Console.WriteLine($"[SIGNAL] {kline.Symbol} ADX={result.Adx:F1} BB={result.BbLower:F2} RSI={result.Rsi:F1} Close={kline.Close}");
            // 暫存成交價與前收盤，供 HandleTradeReport 使用
            _pendingFillPrice[kline.Symbol] = kline.Close;
            if (kline.PreviousClose.HasValue)
                _pendingPreviousClose[kline.Symbol] = kline.PreviousClose.Value;
            await _broker.PlaceMarketBuyAsync(kline.Symbol, DefaultQty);
        }
    }

    // Mock 環境用：暫存預期成交價 & 前收盤
    private readonly Dictionary<string, decimal> _pendingFillPrice = new();
    private readonly Dictionary<string, decimal> _pendingPreviousClose = new();

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

    public static decimal CalculateTakeProfit(decimal fillPrice) => Math.Round(fillPrice * 1.005m, 2);
    public static decimal CalculateStopLoss(decimal fillPrice) => Math.Round(fillPrice * 0.990m, 2);
}
