namespace DayTradeBot.Capital.Interfaces;

public record OrderResult(bool Success, string OrderId, string Message);

public record OcoParams(
    string Symbol,
    long Qty,
    decimal TakeProfitPrice,
    decimal StopLossPrice
);

/// <summary>
/// 下單服務介面。
/// 生產實作：SKOrderWrapper（群益 SKOrderLib COM）
/// 測試/DryRun 實作：MockBrokerApi
/// </summary>
public interface IOrderService
{
    /// <summary>現貨市價買進</summary>
    Task<OrderResult> PlaceMarketBuyAsync(string symbol, long qty);

    /// <summary>
    /// 送出主機端 OCO 二擇一條件單（停利 MIT + 停損）。
    /// 群益主機端智慧單：一邊觸發後，另一邊自動由主機取消。
    /// </summary>
    Task<OrderResult> PlaceOcoOrderAsync(OcoParams order);

    /// <summary>取消委託單</summary>
    Task<bool> CancelOrderAsync(string orderId);

    /// <summary>成交回報事件（含成交均價）</summary>
    event EventHandler<TradeReportArgs> OnTradeReport;

    bool IsConnected { get; }
}

public record TradeReportArgs(
    string OrderId,
    string Symbol,
    decimal FillPrice,
    long FilledQty,
    DateTime FilledAt,
    string OrderType  // "BUY" | "SELL_TP" | "SELL_SL"
);
