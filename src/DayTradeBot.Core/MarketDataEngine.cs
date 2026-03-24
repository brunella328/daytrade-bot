using System.Collections.Concurrent;
using DayTradeBot.Core.Models;

namespace DayTradeBot.Core;

/// <summary>
/// 接收 Tick 串流（透過 ConcurrentQueue），
/// 即時合成 1-min OHLCV K線，每根收盤時觸發 OnKLineClosed。
/// </summary>
public class MarketDataEngine
{
    public event EventHandler<KLine>? OnKLineClosed;
    public event EventHandler<TickData>? OnTickEnqueued;

    private readonly ConcurrentQueue<TickData> _tickQueue = new();
    private readonly Dictionary<string, KLine> _openCandles = new();
    private readonly Dictionary<string, decimal> _referencePrices = new();
    private CancellationToken _ct;

    // ── 參考價（昨日收盤）管理 ─────────────────────────────────────────────

    /// <summary>設定標的的參考價（昨日收盤），供 FugleMarketDataWrapper 在訂閱後呼叫。</summary>
    public void SetReferencePrice(string symbol, decimal price)
    {
        if (price > 0) _referencePrices[symbol] = price;
    }

    /// <summary>取得標的的參考價，若尚未設定則回傳 null。</summary>
    public decimal? GetReferencePrice(string symbol) =>
        _referencePrices.TryGetValue(symbol, out var p) ? p : null;

    public void EnqueueTick(TickData tick)
    {
        _tickQueue.Enqueue(tick);
        OnTickEnqueued?.Invoke(this, tick);
    }

    /// <summary>啟動消費者 loop，直到 cancellationToken 取消</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ct = cancellationToken;
        return Task.Run(ConsumeLoop, cancellationToken);
    }

    private async Task ConsumeLoop()
    {
        try
        {
            while (!_ct.IsCancellationRequested)
            {
                while (_tickQueue.TryDequeue(out var tick))
                    ProcessTick(tick);

                await Task.Delay(10, _ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }

        // 排空剩餘 Tick
        while (_tickQueue.TryDequeue(out var tick))
            ProcessTick(tick);
    }

    private void ProcessTick(TickData tick)
    {
        var minuteStart = new DateTime(
            tick.Timestamp.Year, tick.Timestamp.Month, tick.Timestamp.Day,
            tick.Timestamp.Hour, tick.Timestamp.Minute, 0);

        if (_openCandles.TryGetValue(tick.Symbol, out var candle))
        {
            if (minuteStart > candle.OpenTime)
            {
                // 新的分鐘開始 → 結算上一根 K線
                candle.CloseTime = candle.OpenTime.AddMinutes(1).AddSeconds(-1);
                OnKLineClosed?.Invoke(this, candle);

                // 開新 K線
                _openCandles[tick.Symbol] = NewCandle(tick, minuteStart);
            }
            else
            {
                // 同一分鐘 → 更新 OHLCV
                candle.High = Math.Max(candle.High, tick.Price);
                candle.Low = Math.Min(candle.Low, tick.Price);
                candle.Close = tick.Price;
                candle.Volume += tick.Volume;
            }
        }
        else
        {
            _openCandles[tick.Symbol] = NewCandle(tick, minuteStart);
        }
    }

    private KLine NewCandle(TickData tick, DateTime minuteStart) => new()
    {
        Symbol        = tick.Symbol,
        Open          = tick.Price,
        High          = tick.Price,
        Low           = tick.Price,
        Close         = tick.Price,
        Volume        = tick.Volume,
        OpenTime      = minuteStart,
        PreviousClose = _referencePrices.TryGetValue(tick.Symbol, out var refP) ? refP : null
    };

    /// <summary>強制結算所有未收盤的 K線（用於收盤強制平倉前）</summary>
    public IEnumerable<KLine> FlushOpenCandles()
    {
        foreach (var (symbol, candle) in _openCandles)
        {
            candle.CloseTime = DateTime.Now;
            yield return candle;
        }
        _openCandles.Clear();
    }
}
