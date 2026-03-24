using DayTradeBot.Core;
using DayTradeBot.Core.Broker;
using DayTradeBot.Storage;
using DayTradeBot.Api;

var builder = WebApplication.CreateBuilder(args);

var dbPath        = builder.Configuration["DbPath"] ?? "data/trades.db";
var tradingConfig = builder.Configuration.GetSection("TradingConfig").Get<TradingConfig>() ?? new TradingConfig();
builder.Services.AddSingleton(tradingConfig);

// 讀取初始 watchlist
var watchlistPath = builder.Configuration["WatchlistPath"] ?? "watchlist.json";
var initialSymbols = File.Exists(watchlistPath)
    ? System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(watchlistPath)) ?? []
    : new[] { "2330", "2317", "2454" };

builder.Services.AddSingleton<WatchlistManager>(_ => new WatchlistManager(initialSymbols));

// 核心引擎
builder.Services.AddSingleton<MarketDataEngine>();
builder.Services.AddSingleton<IndicatorEngine>();
builder.Services.AddSingleton<RawTickBuffer>();
builder.Services.AddSingleton(_ => new TradeRepository(dbPath));

// HttpClient（選股用）
builder.Services.AddHttpClient("screener", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "DayTradeBot/1.0");
    client.Timeout = TimeSpan.FromSeconds(30);
});

if (tradingConfig.IsLive)
{
    // ── Live 模式：Fugle 行情 + Fugle 交易 ────────────────────────────
    var fugleConfig = builder.Configuration.GetSection("Fugle").Get<DayTradeBot.Fugle.FugleConfig>()
        ?? throw new InvalidOperationException("Live 模式需設定 Fugle ApiKey");

    builder.Services.AddSingleton(fugleConfig);
    builder.Services.AddSingleton<StockScreener>(sp =>
        new StockScreener(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("screener"),
            fugleConfig.ApiKey,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StockScreener>>()));
    builder.Services.AddSingleton<IBrokerApi, DayTradeBot.Fugle.FugleTradeWrapper>();
    builder.Services.AddHostedService<DayTradeBot.Fugle.FugleMarketDataWrapper>();
    builder.Services.AddSingleton<ScreenerScheduler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ScreenerScheduler>());
}
else if (tradingConfig.IsPaperTrade)
{
    // ── PaperTrade 模式：Fugle 真實行情 + 模擬交易 ────────────────────
    var fugleConfig = builder.Configuration.GetSection("Fugle").Get<DayTradeBot.Fugle.FugleConfig>()
        ?? throw new InvalidOperationException("PaperTrade 模式需設定 Fugle ApiKey");

    builder.Services.AddSingleton(fugleConfig);
    builder.Services.AddSingleton<StockScreener>(sp =>
        new StockScreener(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("screener"),
            fugleConfig.ApiKey,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StockScreener>>()));
    builder.Services.AddSingleton<IBrokerApi, MockBrokerApi>();
    builder.Services.AddHostedService<DayTradeBot.Fugle.FugleMarketDataWrapper>();
    builder.Services.AddSingleton<ScreenerScheduler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ScreenerScheduler>());
}
else
{
    // ── DryRun 模式：假行情 + 模擬交易（不跑選股）───────────────────
    builder.Services.AddSingleton<IBrokerApi, MockBrokerApi>();
    builder.Services.AddHostedService<MockTickProducer>();
}

// LocalRiskManager + StrategyBrain + PaperPortfolio + BacktestRunner（三種模式都需要）
builder.Services.AddSingleton<LocalRiskManager>();
builder.Services.AddSingleton<BacktestRunner>();
builder.Services.AddSingleton<StrategyBrain>(sp =>
    new StrategyBrain(
        sp.GetRequiredService<IBrokerApi>(),
        sp.GetRequiredService<IndicatorEngine>()));
builder.Services.AddSingleton<PaperPortfolio>();
builder.Services.AddHostedService<TradingEngine>();

builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// 掛接事件
var rawBuffer    = app.Services.GetRequiredService<RawTickBuffer>();
var marketEngine = app.Services.GetRequiredService<MarketDataEngine>();
var portfolio    = app.Services.GetRequiredService<PaperPortfolio>();
var brain        = app.Services.GetRequiredService<StrategyBrain>();
var riskMgr      = app.Services.GetRequiredService<LocalRiskManager>();

marketEngine.OnTickEnqueued += (_, tick) => rawBuffer.Add(tick);
marketEngine.OnTickEnqueued += riskMgr.OnTick;
brain.OnPositionOpened      += portfolio.OnPositionOpened;
riskMgr.OnPositionExited    += portfolio.OnPositionExited;

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── API Endpoints ──────────────────────────────────────────────────────
app.MapGet("/api/trades", async (TradeRepository repo) =>
    Results.Ok(await repo.GetAllTradesAsync()));

app.MapGet("/api/stats", async (TradeRepository repo) =>
    Results.Ok(await repo.GetStatsAsync()));

app.MapGet("/api/health", (TradingConfig cfg) =>
    Results.Ok(new { status = "ok", mode = cfg.Mode, time = DateTime.Now }));

app.MapGet("/api/raw-ticks", (RawTickBuffer buf) =>
    Results.Ok(buf.GetAll()));

app.MapGet("/api/debug/indicators", (StrategyBrain b) =>
    Results.Ok(b.LastIndicators.Values.OrderBy(x => x.Symbol)));

app.MapGet("/api/strategy/config", (StrategyBrain b) =>
    Results.Ok(b.Config));

app.MapPost("/api/strategy/config", (StrategyConfig req, StrategyBrain b) =>
{
    b.Config.AdxThreshold   = req.AdxThreshold;
    b.Config.RsiThreshold   = req.RsiThreshold;
    b.Config.UseBbCondition = req.UseBbCondition;
    b.Config.TakeProfitPct  = req.TakeProfitPct;
    b.Config.StopLossPct    = req.StopLossPct;
    b.Config.EntryStartHour = req.EntryStartHour;
    b.Config.EntryEndHour   = req.EntryEndHour;
    b.Config.ForceCloseHour = req.ForceCloseHour;
    b.Config.TotalCapital      = req.TotalCapital;
    b.Config.MaxPositionPct    = req.MaxPositionPct;
    b.Config.RedZoneThreshold  = req.RedZoneThreshold;
    return Results.Ok(b.Config);
});

app.MapGet("/api/portfolio", (PaperPortfolio p) =>
    Results.Ok(p.GetSnapshot()));

app.MapPost("/api/portfolio/reset", (ResetRequest req, PaperPortfolio p) =>
{
    if (req.Capital <= 0) return Results.BadRequest(new { error = "本金必須大於 0" });
    p.Reset(req.Capital);
    return Results.Ok(p.GetSnapshot());
});

app.MapGet("/api/watchlist", (WatchlistManager wl) =>
    Results.Ok(wl.GetStatus()));

app.MapPost("/api/watchlist/refresh", async (ScreenerScheduler? sched, WatchlistManager wl) =>
{
    if (sched is null)
        return Results.BadRequest(new { error = "DryRun 模式不支援選股" });
    await sched.RunScreenerAsync();
    return Results.Ok(wl.GetStatus());
});

app.MapPost("/api/backtest", async (
    RawTickBuffer buffer,
    StrategyBrain brain,
    MarketDataEngine market,
    BacktestRunner runner) =>
{
    var ticks = buffer.GetAll();
    if (ticks.Count == 0)
        return Results.BadRequest(new { error = "目前無 Tick 資料，請在盤中執行" });

    // 從 MarketDataEngine 取本日已快取的參考價
    var refPrices = ticks
        .Select(t => t.Symbol).Distinct()
        .Select(s => (Symbol: s, Price: market.GetReferencePrice(s)))
        .Where(x => x.Price.HasValue)
        .ToDictionary(x => x.Symbol, x => x.Price!.Value);

    // Task.Run 避免 .GetAwaiter().GetResult() 在請求執行緒上 deadlock
    var result = await Task.Run(() => runner.Run(ticks, brain.Config, refPrices));
    return Results.Ok(result);
});

// Debug：注入假交易（僅非 Live）
app.MapPost("/api/debug/inject-trade", async (TradeRepository repo, TradingConfig config) =>
{
    if (config.IsLive) return Results.Forbid();
    var rng = new Random();
    var symbols = new[] { "2330", "2317", "2454" };
    var reasons = new[] { "TP", "SL", "ForceClose" };
    for (int i = 0; i < 5; i++)
    {
        var symbol     = symbols[rng.Next(symbols.Length)];
        var entryPrice = Math.Round(500m + (decimal)(rng.NextDouble() * 300), 2);
        var exitReason = reasons[rng.Next(reasons.Length)];
        var exitPrice  = exitReason == "TP" ? Math.Round(entryPrice * 1.005m, 2)
                       : exitReason == "SL" ? Math.Round(entryPrice * 0.990m, 2)
                       : entryPrice;
        var gross      = Math.Round((exitPrice - entryPrice) * 1, 2);
        var commission = Math.Round((entryPrice + exitPrice) * 0.001425m, 2);
        var tax        = Math.Round(exitPrice * 0.003m, 2);
        await repo.InsertTradeAsync(new TradeRecord
        {
            Symbol = symbol, EntryPrice = entryPrice, ExitPrice = exitPrice, Qty = 1,
            EntryTime = DateTime.Now.AddMinutes(-rng.Next(5, 60)),
            ExitTime  = DateTime.Now.AddMinutes(-rng.Next(1, 4)),
            GrossPnL  = gross, Commission = commission, Tax = tax,
            NetPnL    = Math.Round(gross - commission - tax, 2),
            ExitReason = exitReason
        });
    }
    return Results.Ok(new { injected = 5 });
});

app.Run();

record ResetRequest(decimal Capital);
