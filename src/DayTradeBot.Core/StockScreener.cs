using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Skender.Stock.Indicators;

namespace DayTradeBot.Core;

/// <summary>
/// 每日選股邏輯：
/// ① TWSE OpenAPI 抓前日全市場數據
/// ② 基本篩選（價格、成交量、振幅）
/// ③ 前 20 名用 Fugle REST API 補 RSI
/// ④ 評分排序，回傳前 N 名
/// </summary>
public class StockScreener
{
    private readonly HttpClient _http;
    private readonly string _fugleApiKey;
    private readonly ILogger<StockScreener> _logger;

    private const int    MaxSubscriptions = 5;
    private const decimal MinPrice        = 50m;
    private const decimal MaxPrice        = 3000m;
    private const long   MinVolumeLots    = 3000;   // 張
    private const double MinRangePct      = 1.0;    // 振幅 %

    public StockScreener(HttpClient http, string fugleApiKey, ILogger<StockScreener> logger)
    {
        _http        = http;
        _fugleApiKey = fugleApiKey;
        _logger      = logger;
    }

    public async Task<IReadOnlyList<ScreenerEntry>> RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[Screener] 開始選股...");

        // ① 抓 TWSE 前日全市場數據
        var twseRows = await FetchTwseAsync(ct);
        _logger.LogInformation("[Screener] TWSE 共 {N} 檔", twseRows.Count);

        // ② 基本篩選
        var candidates = twseRows
            .Where(r => r.Close >= MinPrice && r.Close <= MaxPrice
                     && r.VolumeLots >= MinVolumeLots
                     && r.RangePct >= MinRangePct)
            .ToList();
        _logger.LogInformation("[Screener] 基本篩選後 {N} 檔", candidates.Count);

        // ③ 取前 20 補 RSI（減少 API 呼叫數）
        var top20 = candidates
            .OrderByDescending(r => r.LiquidityScore + r.VolatilityScore)
            .Take(20)
            .ToList();

        var entries = new List<ScreenerEntry>();
        foreach (var r in top20)
        {
            var rsi = await FetchRsiAsync(r.Symbol, ct);
            var rsiScore = RsiScore(rsi);

            var score = r.LiquidityScore * 0.4 + r.VolatilityScore * 0.4 + rsiScore * 0.2;
            var reason = $"量={r.VolumeLots:N0}張 振幅={r.RangePct:F1}% RSI={rsi?.ToString("F1") ?? "—"} 分={score:F2}";

            entries.Add(new ScreenerEntry(r.Symbol, r.Name, r.Close,
                r.VolumeLots, r.RangePct, rsi, score, reason));

            await Task.Delay(150, ct); // 避免 API rate limit
        }

        var result = entries
            .OrderByDescending(e => e.Score)
            .Take(MaxSubscriptions)
            .ToList();

        _logger.LogInformation("[Screener] 選出 {N} 檔：{Syms}",
            result.Count, string.Join(", ", result.Select(e => e.Symbol)));

        return result;
    }

    // ── TWSE OpenAPI ───────────────────────────────────────────────────────

    private async Task<List<TwseRow>> FetchTwseAsync(CancellationToken ct)
    {
        try
        {
            var url  = "https://openapi.twse.com.tw/v1/exchangeReport/STOCK_DAY_ALL";
            var raw  = await _http.GetFromJsonAsync<List<TwseRaw>>(url, ct) ?? [];
            var rows = new List<TwseRow>();

            // 全市場最大值（正規化用）
            double maxVol = 1, maxRange = 1;

            foreach (var r in raw)
            {
                if (!TryParse(r.ClosingPrice, out var close) || close <= 0) continue;
                if (!TryParse(r.HighestPrice, out var high))  continue;
                if (!TryParse(r.LowestPrice,  out var low))   continue;
                if (!TryParseLong(r.TradeVolume, out var vol)) continue;

                var lots     = vol / 1000;
                var rangePct = high > 0 ? (double)((high - low) / close * 100) : 0;

                if (maxVol   < (double)lots)     maxVol   = (double)lots;
                if (maxRange < rangePct)          maxRange = rangePct;

                rows.Add(new TwseRow(r.Code, r.Name ?? r.Code, close, lots, rangePct, 0, 0));
            }

            // 正規化分數
            return rows
                .Select(r => r with {
                    LiquidityScore  = (double)r.VolumeLots / maxVol,
                    VolatilityScore = r.RangePct / maxRange
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Screener] TWSE API 失敗：{Msg}", ex.Message);
            return [];
        }
    }

    // ── Fugle REST API（RSI）──────────────────────────────────────────────

    private async Task<double?> FetchRsiAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.fugle.tw/marketdata/v1.0/stock/historical/candles/{symbol}" +
                      $"?timeframe=D&limit=20&apikey={_fugleApiKey}";
            var resp = await _http.GetFromJsonAsync<FugleCandles>(url, ct);
            if (resp?.Data is null || resp.Data.Count < 14) return null;

            var closes = resp.Data
                .OrderBy(c => c.Date)
                .Select(c => (double)c.Close)
                .ToList();

            return CalcRsi(closes, 14);
        }
        catch
        {
            return null;
        }
    }

    // ── RSI 計算 ──────────────────────────────────────────────────────────

    private static double CalcRsi(List<double> closes, int period)
    {
        if (closes.Count < period + 1) return 50;

        var baseDate = new DateTime(2000, 1, 1);
        var quotes = closes
            .Select((c, i) => new Quote
            {
                Date   = baseDate.AddDays(i),
                Open   = (decimal)c,
                High   = (decimal)c,
                Low    = (decimal)c,
                Close  = (decimal)c,
                Volume = 0
            })
            .ToList();

        return quotes.GetRsi(period).LastOrDefault()?.Rsi ?? 50;
    }

    // RSI 加分：40–55 之間得滿分（最容易在明天跌破閾值）
    private static double RsiScore(double? rsi)
    {
        if (rsi is null) return 0.5;
        if (rsi >= 40 && rsi <= 55) return 1.0;
        if (rsi is > 55 and <= 65)  return 0.6;
        if (rsi is > 30 and < 40)   return 0.7;
        return 0.3;
    }

    // ── 輔助 ─────────────────────────────────────────────────────────────

    private static bool TryParse(string? s, out decimal v)
    {
        v = 0;
        return !string.IsNullOrWhiteSpace(s) &&
               decimal.TryParse(s.Replace(",", ""), out v);
    }

    private static bool TryParseLong(string? s, out long v)
    {
        v = 0;
        return !string.IsNullOrWhiteSpace(s) &&
               long.TryParse(s.Replace(",", ""), out v);
    }
}

// ── 模型 ─────────────────────────────────────────────────────────────────

internal record TwseRow(
    string  Symbol,
    string  Name,
    decimal Close,
    long    VolumeLots,
    double  RangePct,
    double  LiquidityScore,
    double  VolatilityScore
);

file class TwseRaw
{
    [JsonPropertyName("Code")]          public string? Code         { get; set; }
    [JsonPropertyName("Name")]          public string? Name         { get; set; }
    [JsonPropertyName("TradeVolume")]   public string? TradeVolume  { get; set; }
    [JsonPropertyName("HighestPrice")]  public string? HighestPrice { get; set; }
    [JsonPropertyName("LowestPrice")]   public string? LowestPrice  { get; set; }
    [JsonPropertyName("ClosingPrice")]  public string? ClosingPrice { get; set; }
}

file class FugleCandles
{
    [JsonPropertyName("data")]
    public List<FugleCandle>? Data { get; set; }
}

file class FugleCandle
{
    [JsonPropertyName("date")]  public string? Date  { get; set; }
    [JsonPropertyName("close")] public decimal Close { get; set; }
}
