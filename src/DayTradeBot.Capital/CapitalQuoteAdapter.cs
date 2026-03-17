using DayTradeBot.Capital.Interfaces;
using DayTradeBot.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DayTradeBot.Capital;

/// <summary>
/// Live 模式下替換 MockTickProducer。
/// 將群益 SKQuoteWrapper 的 OnTickReceived 橋接至 MarketDataEngine。
/// </summary>
public class CapitalQuoteAdapter : BackgroundService
{
    private readonly IQuoteService _quoteService;
    private readonly MarketDataEngine _engine;
    private readonly ILogger<CapitalQuoteAdapter> _logger;
    private readonly IEnumerable<string> _watchlist;

    public CapitalQuoteAdapter(
        IQuoteService quoteService,
        MarketDataEngine engine,
        ILogger<CapitalQuoteAdapter> logger,
        IEnumerable<string> watchlist)
    {
        _quoteService = quoteService;
        _engine = engine;
        _logger = logger;
        _watchlist = watchlist;

        // 群益 Tick → MarketDataEngine ConcurrentQueue
        _quoteService.OnTickReceived += (_, tick) => _engine.EnqueueTick(tick);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Capital] 開始訂閱報價：{Symbols}", string.Join(",", _watchlist));
        await _quoteService.SubscribeAsync(_watchlist, stoppingToken);

        // 保持存活直到取消
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
