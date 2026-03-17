using DayTradeBot.Core.Broker;
using DayTradeBot.Core.Models;

namespace DayTradeBot.Core;

/// <summary>
/// 本地 OCO 監控器（Fugle 無主機端智慧單的替代方案）。
///
/// 流程：
/// 1. StrategyBrain 買進成交後呼叫 RegisterPosition
/// 2. LocalRiskManager 訂閱報價事件（OnTickReceived）
/// 3. 每筆新 Tick 檢查所有持倉：
///    - 最新價 >= 成本 × 1.005 → 觸發停利，送市價賣單
///    - 最新價 <= 成本 × 0.990 → 觸發停損，送市價賣單
/// 4. 觸發後從監控清單移除，透過 OnPositionExited 通知 TradingEngine 寫 DB
///
/// ⚠️  進程崩潰時部位監控會中斷，務必確保 app 穩定運行。
///     生產環境應搭配 watchdog / 系統服務自動重啟。
/// </summary>
public class LocalRiskManager
{
    private readonly IBrokerApi _broker;
    private readonly Dictionary<string, ManagedPosition> _positions = new();
    private readonly object _lock = new();

    // 最新成交價快取（symbol → price）
    private readonly Dictionary<string, decimal> _latestPrices = new();

    public event EventHandler<PositionExitArgs>? OnPositionExited;

    public LocalRiskManager(IBrokerApi broker)
    {
        _broker = broker;
    }

    /// <summary>
    /// 買進成交後登記部位。
    /// 由 StrategyBrain.OnPositionOpened 觸發。
    /// </summary>
    public void RegisterPosition(string symbol, decimal fillPrice, long qty, decimal tpPrice, decimal slPrice)
    {
        lock (_lock)
        {
            _positions[symbol] = new ManagedPosition(symbol, fillPrice, qty, tpPrice, slPrice, DateTime.Now);
            Console.WriteLine($"[RiskManager] 登記部位 {symbol} fill={fillPrice} TP={tpPrice} SL={slPrice}");
        }
    }

    /// <summary>
    /// 接收新 Tick，檢查所有持倉的 TP/SL 條件。
    /// 由 FugleMarketDataWrapper.OnTickReceived 事件呼叫。
    /// </summary>
    public void OnTick(object? sender, TickData tick)
    {
        lock (_lock)
        {
            _latestPrices[tick.Symbol] = tick.Price;

            if (!_positions.TryGetValue(tick.Symbol, out var pos)) return;

            string? exitReason = null;

            if (tick.Price >= pos.TakeProfitPrice)
                exitReason = "TP";
            else if (tick.Price <= pos.StopLossPrice)
                exitReason = "SL";

            if (exitReason is not null)
                TriggerExit(pos, tick.Price, exitReason);
        }
    }

    /// <summary>強制平倉所有部位（13:00 收盤用）</summary>
    public void ForceCloseAll()
    {
        lock (_lock)
        {
            foreach (var pos in _positions.Values.ToList())
            {
                var price = _latestPrices.TryGetValue(pos.Symbol, out var p) ? p : pos.FillPrice;
                TriggerExit(pos, price, "ForceClose");
            }
        }
    }

    private void TriggerExit(ManagedPosition pos, decimal exitPrice, string reason)
    {
        _positions.Remove(pos.Symbol);
        Console.WriteLine($"[RiskManager] {reason} 觸發 {pos.Symbol} @ {exitPrice}");

        // 送市價賣單（fire-and-forget）
        _ = _broker.PlaceMarketSellAsync(pos.Symbol, pos.Qty);

        // 通知 TradingEngine 寫 DB
        OnPositionExited?.Invoke(this, new PositionExitArgs(
            pos.Symbol, pos.FillPrice, exitPrice, pos.Qty, pos.EntryTime, reason));
    }

    public bool HasPosition(string symbol)
    {
        lock (_lock) { return _positions.ContainsKey(symbol); }
    }

    public decimal? GetLatestPrice(string symbol)
    {
        lock (_lock)
        {
            return _latestPrices.TryGetValue(symbol, out var p) ? p : null;
        }
    }
}

public record ManagedPosition(
    string Symbol,
    decimal FillPrice,
    long Qty,
    decimal TakeProfitPrice,
    decimal StopLossPrice,
    DateTime EntryTime
);

public record PositionExitArgs(
    string Symbol,
    decimal EntryPrice,
    decimal ExitPrice,
    long Qty,
    DateTime EntryTime,
    string Reason
);
