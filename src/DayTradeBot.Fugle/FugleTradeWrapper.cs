using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DayTradeBot.Core.Broker;

namespace DayTradeBot.Fugle;

/// <summary>
/// Fugle Trade API REST 下單封裝。
/// 文件：https://developer.fugle.tw/docs/trading/overview
///
/// ⚠️  EdDSA (Ed25519) 簽章目前預留 TODO。
///     正式使用前需實作：
///     1. 載入 .pem 私鑰
///     2. 計算 UNIX timestamp + nonce
///     3. 簽署 payload → Base64 → Authorization header
/// </summary>
public class FugleTradeWrapper : IBrokerApi
{
    public event EventHandler<TradeReport>? OnTradeReport;

    private readonly HttpClient _http;
    private readonly FugleConfig _config;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FugleTradeWrapper(FugleConfig config)
    {
        _config = config;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.fugle.tw/trade/v3.0/")
        };
        _http.DefaultRequestHeaders.Add("X-API-KEY", config.ApiKey);
    }

    /// <summary>
    /// 現貨市價買進。
    /// Fugle Trade API：POST /order
    /// </summary>
    public async Task<string> PlaceMarketBuyAsync(string symbol, long qty)
    {
        var orderId = Guid.NewGuid().ToString("N")[..12];
        var payload = new FugleOrderRequest
        {
            StockNo = symbol,
            BuySell = "B",          // B=買進
            TradeType = "0",        // 0=現股
            PriceFlag = "4",        // 4=市價
            Price = "0",
            Quantity = (int)qty,
            ApCode = "1",           // 1=普通
            TradeDate = DateTime.Now.ToString("yyyyMMdd")
        };

        // TODO: 加入 EdDSA 簽章 header
        // AddEdDsaSignature(_http, payload);

        try
        {
            var resp = await _http.PostAsJsonAsync("order", payload, JsonOpts);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Fugle] 買進失敗 {symbol}：{resp.StatusCode} {body}");
                return orderId;
            }

            var result = JsonSerializer.Deserialize<FugleOrderResponse>(body, JsonOpts);
            var actualOrderId = result?.OrderNo ?? orderId;

            Console.WriteLine($"[Fugle] 買進送出 {symbol} qty={qty} orderNo={actualOrderId}");

            // Fugle 目前無 WebSocket 成交回報，用輪詢確認成交
            _ = PollOrderFillAsync(actualOrderId, symbol, qty);

            return actualOrderId;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fugle] 下單例外 {symbol}：{ex.Message}");
            return orderId;
        }
    }

    /// <summary>
    /// 平倉市價賣出（供 LocalRiskManager 呼叫）。
    /// </summary>
    public async Task<string> PlaceMarketSellAsync(string symbol, long qty)
    {
        var payload = new FugleOrderRequest
        {
            StockNo = symbol,
            BuySell = "S",
            TradeType = "0",
            PriceFlag = "4",
            Price = "0",
            Quantity = (int)qty,
            ApCode = "1",
            TradeDate = DateTime.Now.ToString("yyyyMMdd")
        };

        // TODO: 加入 EdDSA 簽章 header
        try
        {
            var resp = await _http.PostAsJsonAsync("order", payload, JsonOpts);
            var body = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<FugleOrderResponse>(body, JsonOpts);
            var orderId = result?.OrderNo ?? "";
            Console.WriteLine($"[Fugle] 賣出送出 {symbol} qty={qty} orderNo={orderId}");
            return orderId;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fugle] 賣出例外：{ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Fugle 無原生 OCO。
    /// 此方法向 LocalRiskManager 登記部位即可，不需送任何 API。
    /// LocalRiskManager 負責監控報價並在 TP/SL 觸發時呼叫 PlaceMarketSellAsync。
    /// </summary>
    public Task<OcoOrderResult> PlaceOcoOrderAsync(string symbol, long qty, decimal tp, decimal sl)
    {
        // LocalRiskManager 已在 StrategyBrain.OnPositionOpened 訂閱，無需額外操作
        Console.WriteLine($"[Fugle] OCO 由本地 LocalRiskManager 監控 {symbol} TP={tp} SL={sl}");
        return Task.FromResult(new OcoOrderResult($"LOCAL_TP_{symbol}", $"LOCAL_SL_{symbol}"));
    }

    public Task CancelOrderAsync(string orderId)
    {
        // TODO: DELETE /order/{orderId}
        Console.WriteLine($"[Fugle] 取消委託 {orderId}（TODO）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 輪詢委託狀態，確認完全成交後觸發 OnTradeReport。
    /// Fugle 無 WebSocket 成交回報，改用輪詢 GET /order/{orderNo}。
    /// </summary>
    private async Task PollOrderFillAsync(string orderNo, string symbol, long qty)
    {
        for (int i = 0; i < 30; i++) // 最多等 30 秒
        {
            await Task.Delay(1000);
            try
            {
                var resp = await _http.GetAsync($"order/{orderNo}");
                if (!resp.IsSuccessStatusCode) continue;

                var body = await resp.Content.ReadAsStringAsync();
                var status = JsonSerializer.Deserialize<FugleOrderStatus>(body, JsonOpts);

                if (status?.Status == "F") // F = 完全成交
                {
                    var fillPrice = status.AvgPrice > 0 ? status.AvgPrice : status.Price;
                    var report = new TradeReport(orderNo, symbol, fillPrice, qty, DateTime.Now);
                    OnTradeReport?.Invoke(this, report);
                    Console.WriteLine($"[Fugle] 成交確認 {symbol} @ {fillPrice}");
                    return;
                }
            }
            catch { /* 繼續輪詢 */ }
        }
        Console.WriteLine($"[Fugle] 警告：{orderNo} 30 秒內未確認成交");
    }

    // ── TODO：EdDSA 簽章 ─────────────────────────────────────────────────
    // private void AddEdDsaSignature(HttpClient client, object payload)
    // {
    //     var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    //     var nonce = Guid.NewGuid().ToString("N");
    //     var body = JsonSerializer.Serialize(payload, JsonOpts);
    //     var message = $"{timestamp}\n{nonce}\n{body}";
    //     // var privateKey = LoadEd25519PrivateKey(_config.PrivateKeyPath);
    //     // var signature = Ed25519.Sign(Encoding.UTF8.GetBytes(message), privateKey);
    //     // var sigBase64 = Convert.ToBase64String(signature);
    //     // client.DefaultRequestHeaders.Authorization =
    //     //     new AuthenticationHeaderValue("EdDSA", $"t={timestamp},n={nonce},s={sigBase64}");
    // }
}

// ── Fugle Trade API 資料模型 ─────────────────────────────────────────────

public class FugleOrderRequest
{
    [JsonPropertyName("stock_no")]   public string StockNo { get; set; } = "";
    [JsonPropertyName("buy_sell")]   public string BuySell { get; set; } = "";
    [JsonPropertyName("trade_type")] public string TradeType { get; set; } = "0";
    [JsonPropertyName("price_flag")] public string PriceFlag { get; set; } = "4";
    [JsonPropertyName("price")]      public string Price { get; set; } = "0";
    [JsonPropertyName("quantity")]   public int Quantity { get; set; }
    [JsonPropertyName("ap_code")]    public string ApCode { get; set; } = "1";
    [JsonPropertyName("trade_date")] public string TradeDate { get; set; } = "";
}

public class FugleOrderResponse
{
    [JsonPropertyName("order_no")]   public string? OrderNo { get; set; }
    [JsonPropertyName("ret_code")]   public string? RetCode { get; set; }
    [JsonPropertyName("ret_msg")]    public string? RetMsg { get; set; }
}

public class FugleOrderStatus
{
    [JsonPropertyName("order_no")]   public string? OrderNo { get; set; }
    [JsonPropertyName("status")]     public string? Status { get; set; }  // F=完全成交
    [JsonPropertyName("price")]      public decimal Price { get; set; }
    [JsonPropertyName("avg_price")]  public decimal AvgPrice { get; set; }
    [JsonPropertyName("quantity")]   public int Quantity { get; set; }
    [JsonPropertyName("filled_qty")] public int FilledQty { get; set; }
}
