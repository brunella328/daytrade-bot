using DayTradeBot.Core;
using DayTradeBot.Core.Broker;
using DayTradeBot.Storage;
using DayTradeBot.Api;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["DbPath"] ?? "data/trades.db";
var tradingConfig = builder.Configuration.GetSection("TradingConfig").Get<TradingConfig>() ?? new TradingConfig();
builder.Services.AddSingleton(tradingConfig);

// 讀取 watchlist
var watchlistPath = builder.Configuration["WatchlistPath"] ?? "watchlist.json";
var watchlist = File.Exists(watchlistPath)
    ? System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(watchlistPath)) ?? []
    : new[] { "2330", "2317", "2454" };
builder.Services.AddSingleton<IEnumerable<string>>(watchlist);

// 核心引擎
builder.Services.AddSingleton<MarketDataEngine>();
builder.Services.AddSingleton<IndicatorEngine>();
builder.Services.AddSingleton(_ => new TradeRepository(dbPath));

if (tradingConfig.IsLive)
{
    // ── Live 模式：Fugle API ─────────────────────────────────────────
    var fugleConfig = builder.Configuration.GetSection("Fugle").Get<DayTradeBot.Fugle.FugleConfig>()
        ?? throw new InvalidOperationException("Live 模式需設定 Fugle ApiKey");

    builder.Services.AddSingleton(fugleConfig);
    builder.Services.AddSingleton<IBrokerApi, DayTradeBot.Fugle.FugleTradeWrapper>();
    builder.Services.AddHostedService<DayTradeBot.Fugle.FugleMarketDataWrapper>();
}
else
{
    // ── DryRun 模式：Mock ────────────────────────────────────────────
    builder.Services.AddSingleton<IBrokerApi, MockBrokerApi>();
    builder.Services.AddHostedService<MockTickProducer>();
}

// LocalRiskManager + StrategyBrain（兩種模式都需要）
builder.Services.AddSingleton<LocalRiskManager>();
builder.Services.AddSingleton<StrategyBrain>(sp =>
    new StrategyBrain(
        sp.GetRequiredService<IBrokerApi>(),
        sp.GetRequiredService<IndicatorEngine>()
    ));

builder.Services.AddHostedService<TradingEngine>();

builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Live 模式：把 LocalRiskManager 掛上報價事件
if (tradingConfig.IsLive)
{
    // FugleMarketDataWrapper 啟動後會觸發 OnTickReceived
    // 在此訂閱（BackgroundService 啟動前需先取得 singleton）
    var riskMgr = app.Services.GetRequiredService<LocalRiskManager>();
    // 注意：FugleMarketDataWrapper 在 BackgroundService 中觸發事件，
    // 需在 TradingEngine 啟動時連接（見 TradingEngine.cs）
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── API Endpoints ──────────────────────────────────────
app.MapGet("/api/trades", async (TradeRepository repo) =>
    Results.Ok(await repo.GetAllTradesAsync()));

app.MapGet("/api/stats", async (TradeRepository repo) =>
    Results.Ok(await repo.GetStatsAsync()));

app.MapGet("/api/health", (TradingConfig cfg) =>
    Results.Ok(new { status = "ok", mode = cfg.Mode, time = DateTime.Now }));

// Debug：注入假交易（僅 DryRun）
app.MapPost("/api/debug/inject-trade", async (TradeRepository repo, TradingConfig config) =>
{
    if (config.IsLive) return Results.Forbid();

    var rng = new Random();
    var symbols = new[] { "2330", "2317", "2454" };
    var reasons = new[] { "TP", "SL", "ForceClose" };

    for (int i = 0; i < 5; i++)
    {
        var symbol = symbols[rng.Next(symbols.Length)];
        var entryPrice = Math.Round(500m + (decimal)(rng.NextDouble() * 300), 2);
        var exitReason = reasons[rng.Next(reasons.Length)];
        var exitPrice = exitReason == "TP"
            ? Math.Round(entryPrice * 1.005m, 2)
            : exitReason == "SL"
                ? Math.Round(entryPrice * 0.990m, 2)
                : entryPrice;

        var gross = Math.Round((exitPrice - entryPrice) * 1, 2);
        var commission = Math.Round((entryPrice + exitPrice) * 0.001425m, 2);
        var tax = Math.Round(exitPrice * 0.003m, 2);

        await repo.InsertTradeAsync(new TradeRecord
        {
            Symbol = symbol,
            EntryPrice = entryPrice,
            ExitPrice = exitPrice,
            Qty = 1,
            EntryTime = DateTime.Now.AddMinutes(-rng.Next(5, 60)),
            ExitTime = DateTime.Now.AddMinutes(-rng.Next(1, 4)),
            GrossPnL = gross,
            Commission = commission,
            Tax = tax,
            NetPnL = Math.Round(gross - commission - tax, 2),
            ExitReason = exitReason
        });
    }
    return Results.Ok(new { injected = 5 });
});

app.Run();
