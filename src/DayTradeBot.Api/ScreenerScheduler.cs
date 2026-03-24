using DayTradeBot.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DayTradeBot.Api;

/// <summary>
/// 每個交易日 08:30 自動執行選股，更新 WatchlistManager。
/// 支援手動觸發（POST /api/watchlist/refresh）。
/// </summary>
public class ScreenerScheduler : BackgroundService
{
    private readonly StockScreener _screener;
    private readonly WatchlistManager _watchlist;
    private readonly string _watchlistPath;
    private readonly ILogger<ScreenerScheduler> _logger;

    private static readonly TimeSpan RunAt = TimeSpan.FromHours(8.5); // 08:30

    public ScreenerScheduler(
        StockScreener screener,
        WatchlistManager watchlist,
        IConfiguration config,
        ILogger<ScreenerScheduler> logger)
    {
        _screener      = screener;
        _watchlist     = watchlist;
        _watchlistPath = config["WatchlistPath"] ?? "watchlist.json";
        _logger        = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Screener] 排程啟動，每個交易日 08:30 執行選股");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now     = DateTime.Now;
            var runTime = now.Date + RunAt;

            // 若今天 08:30 已過，改等明天
            if (now >= runTime) runTime = runTime.AddDays(1);

            // 跳過假日
            while (runTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                runTime = runTime.AddDays(1);

            var delay = runTime - DateTime.Now;
            _logger.LogInformation("[Screener] 下次選股：{Time}", runTime.ToString("MM/dd HH:mm"));

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            if (stoppingToken.IsCancellationRequested) break;

            await RunScreenerAsync(stoppingToken);
        }
    }

    public async Task RunScreenerAsync(CancellationToken ct = default)
    {
        try
        {
            var results = await _screener.RunAsync(ct);
            var symbols = results.Select(r => r.Symbol).ToList();

            _watchlist.Update(symbols, results);

            // 持久化到 watchlist.json
            var json = System.Text.Json.JsonSerializer.Serialize(symbols);
            await File.WriteAllTextAsync(_watchlistPath, json, ct);

            _logger.LogInformation("[Screener] 完成，新標的：{Syms}",
                string.Join(", ", symbols));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Screener] 選股失敗");
        }
    }
}
