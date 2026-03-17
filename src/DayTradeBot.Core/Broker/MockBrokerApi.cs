namespace DayTradeBot.Core.Broker;

/// <summary>
/// Dry Run 用的 Mock 券商 API。
/// 收到 Buy 後延遲 100ms 模擬成交，以送單時的價格作為成交價。
/// OCO 單存入記憶體，由 SimulateOcoFill 觸發出場回報。
/// </summary>
public class MockBrokerApi : IBrokerApi
{
    public event EventHandler<TradeReport>? OnTradeReport;

    private readonly Dictionary<string, (decimal TpPrice, decimal SlPrice, long Qty, string Symbol)> _pendingOco = new();

    public async Task<string> PlaceMarketBuyAsync(string symbol, long qty)
    {
        var orderId = Guid.NewGuid().ToString("N")[..8];
        Console.WriteLine($"[MOCK] BUY {symbol} qty={qty} orderId={orderId}");

        // 模擬 100ms 成交延遲
        await Task.Delay(100);

        // 以送單時的市價（呼叫方傳入）作為成交價
        // 實際成交價由呼叫方在 OnTradeReport 裡讀取 FillPrice
        // 這裡用 0 作 placeholder，StrategyBrain 負責傳入實際 K線收盤價
        var report = new TradeReport(orderId, symbol, 0m, qty, DateTime.Now);
        OnTradeReport?.Invoke(this, report);
        return orderId;
    }

    public Task<OcoOrderResult> PlaceOcoOrderAsync(string symbol, long qty, decimal takeProfitPrice, decimal stopLossPrice)
    {
        var tpId = "TP_" + Guid.NewGuid().ToString("N")[..8];
        var slId = "SL_" + Guid.NewGuid().ToString("N")[..8];
        _pendingOco[tpId] = (takeProfitPrice, stopLossPrice, qty, symbol);
        Console.WriteLine($"[MOCK] OCO {symbol} TP={takeProfitPrice:F2} SL={stopLossPrice:F2}");
        return Task.FromResult(new OcoOrderResult(tpId, slId));
    }

    public async Task<string> PlaceMarketSellAsync(string symbol, long qty)
    {
        var orderId = "SELL_" + Guid.NewGuid().ToString("N")[..8];
        Console.WriteLine($"[MOCK] SELL {symbol} qty={qty} orderId={orderId}");
        await Task.Delay(100);
        return orderId;
    }

    public Task CancelOrderAsync(string orderId)
    {
        _pendingOco.Remove(orderId);
        Console.WriteLine($"[MOCK] CANCEL {orderId}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 模擬 OCO 觸發（由 MockTickProducer 或測試呼叫）。
    /// exitReason: "TP" | "SL" | "ForceClose"
    /// </summary>
    public TradeReport? SimulateOcoFill(string tpOrderId, decimal fillPrice, string exitReason)
    {
        if (!_pendingOco.TryGetValue(tpOrderId, out var order)) return null;
        _pendingOco.Remove(tpOrderId);

        var report = new TradeReport(tpOrderId, order.Symbol, fillPrice, order.Qty, DateTime.Now);
        Console.WriteLine($"[MOCK] OCO_FILL {order.Symbol} {exitReason} @ {fillPrice:F2}");
        return report;
    }
}
