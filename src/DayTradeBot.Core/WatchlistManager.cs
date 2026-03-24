namespace DayTradeBot.Core;

/// <summary>
/// 動態 watchlist，支援執行中換標的。
/// FugleMarketDataWrapper 訂閱 OnChanged 事件，自動重連換訂閱。
/// </summary>
public class WatchlistManager
{
    private readonly object _lock = new();
    private List<string> _symbols;
    private IReadOnlyList<ScreenerEntry> _lastResults = [];
    private DateTime? _lastRunAt;

    public event EventHandler? OnChanged;

    public WatchlistManager(IEnumerable<string> initial)
    {
        _symbols = initial.ToList();
    }

    public IReadOnlyList<string> Symbols
    {
        get { lock (_lock) return _symbols.ToList(); }
    }

    public WatchlistStatus GetStatus()
    {
        lock (_lock)
            return new WatchlistStatus(_symbols.ToList(), _lastResults, _lastRunAt);
    }

    public void Update(IReadOnlyList<string> newSymbols, IReadOnlyList<ScreenerEntry> results)
    {
        bool changed;
        lock (_lock)
        {
            changed      = !newSymbols.SequenceEqual(_symbols);
            _symbols     = newSymbols.ToList();
            _lastResults = results;
            _lastRunAt   = DateTime.Now;
        }
        if (changed) OnChanged?.Invoke(this, EventArgs.Empty);
    }
}

public record WatchlistStatus(
    IReadOnlyList<string>       Symbols,
    IReadOnlyList<ScreenerEntry> LastResults,
    DateTime?                   LastRunAt
);

public record ScreenerEntry(
    string  Symbol,
    string  Name,
    decimal Close,
    long    Volume,       // 張
    double  RangePct,     // 振幅 %
    double? Rsi,
    double  Score,
    string  Reason
);
