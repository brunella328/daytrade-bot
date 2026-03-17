using DayTradeBot.Capital.Interfaces;
using DayTradeBot.Core.Broker;

namespace DayTradeBot.Capital;

/// <summary>
/// 將群益 IOrderService 橋接至系統的 IBrokerApi 介面。
/// Live 模式下，DI 容器注入此類別取代 MockBrokerApi。
/// </summary>
public class CapitalBrokerAdapter : IBrokerApi
{
    public event EventHandler<TradeReport>? OnTradeReport;

    private readonly IOrderService _orderService;

    public CapitalBrokerAdapter(IOrderService orderService)
    {
        _orderService = orderService;

        // 橋接群益成交回報 → 系統 IBrokerApi.OnTradeReport
        _orderService.OnTradeReport += (_, args) =>
        {
            if (args.OrderType != "BUY") return; // 只處理買進回報，賣出由 OCO 主機端處理
            var report = new TradeReport(args.OrderId, args.Symbol, args.FillPrice, args.FilledQty, args.FilledAt);
            OnTradeReport?.Invoke(this, report);
        };
    }

    public async Task<string> PlaceMarketBuyAsync(string symbol, long qty)
    {
        var result = await _orderService.PlaceMarketBuyAsync(symbol, qty);
        if (!result.Success)
            Console.WriteLine($"[CapitalAdapter] 買進失敗：{result.Message}");
        return result.OrderId;
    }

    public async Task<OcoOrderResult> PlaceOcoOrderAsync(string symbol, long qty, decimal takeProfitPrice, decimal stopLossPrice)
    {
        var result = await _orderService.PlaceOcoOrderAsync(new OcoParams(symbol, qty, takeProfitPrice, stopLossPrice));
        if (!result.Success)
            Console.WriteLine($"[CapitalAdapter] OCO 失敗：{result.Message}");
        return new OcoOrderResult("TP_" + result.OrderId, "SL_" + result.OrderId);
    }

    public async Task CancelOrderAsync(string orderId)
    {
        await _orderService.CancelOrderAsync(orderId);
    }
}
