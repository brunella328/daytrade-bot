namespace DayTradeBot.Core.Broker;

public record TradeReport(
    string OrderId,
    string Symbol,
    decimal FillPrice,
    long Qty,
    DateTime FilledAt
);

public record OcoOrderResult(string TpOrderId, string SlOrderId);

public interface IBrokerApi
{
    /// <summary>送出市價買單，回傳 OrderId</summary>
    Task<string> PlaceMarketBuyAsync(string symbol, long qty);

    /// <summary>送出 OCO 賣單（停利 + 停損二擇一）</summary>
    Task<OcoOrderResult> PlaceOcoOrderAsync(string symbol, long qty, decimal takeProfitPrice, decimal stopLossPrice);

    /// <summary>取消指定委託單</summary>
    Task CancelOrderAsync(string orderId);

    /// <summary>成交回報事件（進場 Buy 成交後觸發）</summary>
    event EventHandler<TradeReport> OnTradeReport;
}
