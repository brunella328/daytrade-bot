using DayTradeBot.Core.Models;

namespace DayTradeBot.Capital.Interfaces;

/// <summary>
/// 報價服務介面。
/// 生產實作：SKQuoteWrapper（群益 SKQuoteLib COM）
/// 測試/DryRun 實作：MockTickProducer
/// </summary>
public interface IQuoteService
{
    /// <summary>訂閱股票清單，開始接收即時 Tick 推播</summary>
    Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct);

    /// <summary>收到新 Tick 時觸發，由 MarketDataEngine 訂閱</summary>
    event EventHandler<TickData> OnTickReceived;

    /// <summary>連線狀態</summary>
    bool IsConnected { get; }
}
