using System.Net.Http.Json;
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
/// 單一連線，認證後訂閱 WatchlistManager 的所有 symbol。
/// Watchlist 改變時，取消目前 session CTS 觸發重連，以新標的重新訂閱。
/// </summary>
public class FugleMarketDataWrapper : BackgroundService
{
    private readonly MarketDataEngine _engine;
    private readonly FugleConfig _config;
    private readonly WatchlistManager _watchlist;
    private readonly StrategyBrain _brain;
    private readonly ILogger<FugleMarketDataWrapper> _logger;

    private static readonly Uri StreamingUri =
        new("wss://api.fugle.tw/marketdata/v1.0/stock/streaming");

    private CancellationTokenSource? _sessionCts;

    // REST Client 用於 Snapshot 查詢（取昨日收盤）
    private readonly HttpClient _restClient;

    public FugleMarketDataWrapper(
        MarketDataEngine engine,
        FugleConfig config,
        WatchlistManager watchlist,
        StrategyBrain brain,
        ILogger<FugleMarketDataWrapper> logger)
    {
        _engine    = engine;
        _config    = config;
        _watchlist = watchlist;
        _brain     = brain;
        _logger    = logger;

        _restClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.fugle.tw/marketdata/v1.0/stock/")
        };
        _restClient.DefaultRequestHeaders.Add("X-API-KEY", config.ApiKey);

        // Watchlist 變更 → 中斷目前 session，重連以套用新標的
        _watchlist.OnChanged += (_, _) =>
        {
            _logger.LogInformation("[Fugle] Watchlist 更新，重連中...");
            _sessionCts?.Cancel();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var ct = _sessionCts.Token;

            try
            {
                var symbols = _watchlist.Symbols;
                _logger.LogInformation("[Fugle] 行情 WebSocket 啟動，訂閱 {Count} 檔", symbols.Count);
                await RunSessionAsync(symbols, ct);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Watchlist 更新觸發的 cancel → 短暫等待後重連
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("[Fugle] 連線中斷：{Msg}，5 秒後重連", ex.Message);
                await Task.Delay(5000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSessionAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(StreamingUri, ct);
        _logger.LogInformation("[Fugle] WebSocket 已連線");

        // 認證
        await SendJsonAsync(ws, new { @event = "auth", data = new { apikey = _config.ApiKey } }, ct);

        // 等待 authenticated（跳過 heartbeat 等其他訊息）
        FugleEnvelope? authResult = null;
        for (var i = 0; i < 10; i++)
        {
            authResult = await ReceiveMessageAsync(ws, ct);
            if (authResult?.Event == "authenticated") break;
            if (authResult?.Event == "error")
            {
                _logger.LogError("[Fugle] 認證失敗：{Json}", authResult.RawJson);
                return;
            }
        }
        if (authResult?.Event != "authenticated")
        {
            _logger.LogError("[Fugle] 認證失敗，未收到 authenticated");
            return;
        }
        _logger.LogInformation("[Fugle] 認證成功");

        // 訂閱所有 symbol 的 trades channel
        foreach (var symbol in symbols)
        {
            await SendJsonAsync(ws, new
            {
                @event = "subscribe",
                data   = new { channel = "trades", symbol }
            }, ct);
            _logger.LogInformation("[Fugle] 訂閱 {Symbol}", symbol);
        }

        // 取得各標的參考價（昨日收盤），供 Red Zone Guard 使用
        await FetchReferencePricesAsync(symbols, ct);

        // 持續接收
        await ReceiveLoopAsync(ws, ct);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var msg = await ReceiveMessageAsync(ws, ct);
            if (msg is null) break;

            if (msg.Event is "data" or "snapshot")
                ProcessDataMessage(msg);
            else if (msg.Event == "error")
                _logger.LogWarning("[Fugle] 伺服器錯誤：{Json}", msg.RawJson);
        }
    }

    /// <summary>
    /// 透過 Fugle MarketData REST API 批次取得各標的的昨日收盤（referencePrice）。
    /// GET /intraday/quote/{symbol} → 欄位 referencePrice / previousClose
    /// 失敗時靜默略過，Red Zone Guard 在無參考價時僅跳過保護（不阻擋進場）。
    /// </summary>
    private async Task FetchReferencePricesAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        // 查詢大盤（TWII = "0001"）計算今日跌幅，設定到 StrategyBrain
        await FetchTwiiDropPctAsync(ct);

        foreach (var symbol in symbols)
        {
            try
            {
                var resp = await _restClient.GetAsync($"intraday/quote/{symbol}", ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("[Fugle] Snapshot {Symbol} 失敗：{Code}", symbol, resp.StatusCode);
                    continue;
                }

                var quote = await resp.Content.ReadFromJsonAsync<FugleQuoteResponse>(
                    cancellationToken: ct);

                var refPrice = quote?.ReferencePrice ?? quote?.PreviousClose ?? 0m;
                if (refPrice > 0)
                {
                    _engine.SetReferencePrice(symbol, refPrice);
                    _logger.LogInformation("[Fugle] 參考價 {Symbol} = {Price}", symbol, refPrice);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Fugle] Snapshot {Symbol} 例外：{Msg}", symbol, ex.Message);
            }
        }
    }

    private async Task FetchTwiiDropPctAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _restClient.GetAsync("intraday/quote/0001", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("[Fugle] TWII Snapshot 失敗：{Code}", resp.StatusCode);
                return;
            }

            var quote = await resp.Content.ReadFromJsonAsync<FugleQuoteResponse>(cancellationToken: ct);
            var prevClose = quote?.ReferencePrice ?? quote?.PreviousClose ?? 0m;
            var lastPrice = quote?.LastPrice ?? 0m;

            if (prevClose > 0 && lastPrice > 0)
            {
                _brain.TwiiDropPct = (lastPrice - prevClose) / prevClose;
                _logger.LogInformation("[Fugle] TWII 跌幅 = {Pct:P2}", _brain.TwiiDropPct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Fugle] TWII Snapshot 例外：{Msg}", ex.Message);
        }
    }

    private void ProcessDataMessage(FugleEnvelope msg)
    {
        try
        {
            var data = msg.Data;
            if (data is null || data.Price <= 0 || data.Size <= 0) return;

            var symbol    = data.Symbol ?? "UNKNOWN";
            var timestamp = data.Time > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(data.Time / 1000).LocalDateTime
                : DateTime.Now;

            var tick = new TickData(symbol, data.Price, data.Size, timestamp);
            _engine.EnqueueTick(tick);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Fugle] ProcessDataMessage 例外：{Msg}", ex.Message);
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json  = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<FugleEnvelope?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var result = await ws.ReceiveAsync(buffer, ct);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
            return null;
        }

        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        try
        {
            var env = JsonSerializer.Deserialize<FugleEnvelope>(json, JsonOptions);
            if (env is not null) env.RawJson = json;
            return env;
        }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

// ── 訊息模型 ─────────────────────────────────────────────────────────────

public class FugleEnvelope
{
    [JsonPropertyName("event")]   public string?       Event   { get; set; }
    [JsonPropertyName("channel")] public string?       Channel { get; set; }
    [JsonPropertyName("id")]      public string?       Id      { get; set; }
    [JsonPropertyName("data")]    public FugleTickData? Data   { get; set; }
    [JsonIgnore]                  public string?       RawJson { get; set; }
}

public class FugleTickData
{
    [JsonPropertyName("symbol")]           public string?  Symbol           { get; set; }
    [JsonPropertyName("price")]            public decimal  Price            { get; set; }
    [JsonPropertyName("size")]             public long     Size             { get; set; }
    [JsonPropertyName("time")]             public long     Time             { get; set; }
    [JsonPropertyName("bid")]              public decimal  Bid              { get; set; }
    [JsonPropertyName("ask")]              public decimal  Ask              { get; set; }
    [JsonPropertyName("isLimitUpPrice")]   public bool     IsLimitUpPrice   { get; set; }
    [JsonPropertyName("isLimitDownPrice")] public bool     IsLimitDownPrice { get; set; }
}

/// <summary>Fugle MarketData REST GET /intraday/quote/{symbol} 回應（取昨日收盤用）</summary>
public class FugleQuoteResponse
{
    [JsonPropertyName("referencePrice")] public decimal? ReferencePrice { get; set; }
    [JsonPropertyName("previousClose")]  public decimal? PreviousClose  { get; set; }
    [JsonPropertyName("lastPrice")]      public decimal? LastPrice      { get; set; }
    [JsonPropertyName("limitUpPrice")]   public decimal? LimitUpPrice   { get; set; }
    [JsonPropertyName("limitDownPrice")] public decimal? LimitDownPrice { get; set; }
    [JsonPropertyName("symbol")]         public string?  Symbol         { get; set; }
}
