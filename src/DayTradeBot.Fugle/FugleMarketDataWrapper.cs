using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DayTradeBot.Core;
using DayTradeBot.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DayTradeBot.Fugle;

/// <summary>
/// Fugle MarketData WebSocket 報價接收器。
/// 文件：https://developer.fugle.tw/docs/data/marketdata/real-time
///
/// 連線：wss://api.fugle.tw/marketdata/v1.0/stock/intraday/ticker/{symbol}?apikey={apikey}
/// 每個 symbol 一條 WebSocket 連線，收到 tick 後推入 MarketDataEngine.ConcurrentQueue。
/// </summary>
public class FugleMarketDataWrapper : BackgroundService
{
    private readonly MarketDataEngine _engine;
    private readonly FugleConfig _config;
    private readonly ILogger<FugleMarketDataWrapper> _logger;
    private readonly IEnumerable<string> _watchlist;

    // 最新報價快取（供 LocalRiskManager 訂閱用）
    public event EventHandler<TickData>? OnTickReceived;

    public FugleMarketDataWrapper(
        MarketDataEngine engine,
        FugleConfig config,
        IEnumerable<string> watchlist,
        ILogger<FugleMarketDataWrapper> logger)
    {
        _engine = engine;
        _config = config;
        _watchlist = watchlist;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Fugle] 行情 WebSocket 啟動，訂閱 {Count} 檔", _watchlist.Count());

        // 每個 symbol 開一條 WebSocket
        var tasks = _watchlist.Select(symbol =>
            ConnectSymbolAsync(symbol, stoppingToken));

        await Task.WhenAll(tasks);
    }

    private async Task ConnectSymbolAsync(string symbol, CancellationToken ct)
    {
        var uri = new Uri(
            $"wss://api.fugle.tw/marketdata/v1.0/stock/intraday/ticker/{symbol}?apikey={_config.ApiKey}");

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(uri, ct);
                _logger.LogInformation("[Fugle] 已連線 {Symbol}", symbol);

                await ReceiveLoopAsync(ws, symbol, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("[Fugle] {Symbol} 連線中斷：{Msg}，5 秒後重連", symbol, ex.Message);
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, string symbol, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                break;
            }

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            ProcessMessage(json, symbol);
        }
    }

    private void ProcessMessage(string json, string symbol)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<FugleWsMessage>(json, JsonOptions);
            if (msg?.Event != "data" || msg.Data is null) return;

            var data = msg.Data;
            if (data.Price <= 0 || data.Size <= 0) return;

            // 解析 Fugle 時間格式：ISO 8601（含時區）
            var timestamp = data.Time != null
                ? DateTimeOffset.Parse(data.Time).LocalDateTime
                : DateTime.Now;

            var tick = new TickData(symbol, data.Price, data.Size, timestamp);

            // 推入 MarketDataEngine（K 線合成）
            _engine.EnqueueTick(tick);

            // 觸發報價事件（LocalRiskManager 用）
            OnTickReceived?.Invoke(this, tick);
        }
        catch
        {
            // WebSocket 回呼靜默吸收
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

// ── Fugle WebSocket 訊息模型 ─────────────────────────────────────────────

public class FugleWsMessage
{
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("data")]
    public FugleTickData? Data { get; set; }
}

public class FugleTickData
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    /// <summary>成交價</summary>
    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    /// <summary>成交量（股）</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>成交時間 ISO 8601</summary>
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("bid")]
    public decimal Bid { get; set; }

    [JsonPropertyName("ask")]
    public decimal Ask { get; set; }
}
