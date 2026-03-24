using System.Collections.Concurrent;
using DayTradeBot.Core.Models;

namespace DayTradeBot.Core;

/// <summary>
/// 儲存最近 N 筆原始 Tick，供 Dashboard 原始資料頁面查詢。
/// </summary>
public class RawTickBuffer
{
    private const int MaxSize = 20_000;
    private readonly ConcurrentQueue<RawTickEntry> _queue = new();

    public void Add(TickData tick)
    {
        _queue.Enqueue(new RawTickEntry
        {
            Symbol    = tick.Symbol,
            Price     = tick.Price,
            Volume    = tick.Volume,
            Timestamp = tick.Timestamp,
            ReceivedAt = DateTime.Now
        });

        while (_queue.Count > MaxSize)
            _queue.TryDequeue(out _);
    }

    public IReadOnlyList<RawTickEntry> GetAll() =>
        _queue.Reverse().Take(MaxSize).ToList();
}

public class RawTickEntry
{
    public string Symbol    { get; set; } = "";
    public decimal Price    { get; set; }
    public long Volume      { get; set; }
    public DateTime Timestamp  { get; set; }
    public DateTime ReceivedAt { get; set; }
}
