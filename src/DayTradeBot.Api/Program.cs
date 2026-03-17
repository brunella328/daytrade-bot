using DayTradeBot.Core;
using DayTradeBot.Core.Broker;
using DayTradeBot.Storage;
using DayTradeBot.Api;

var builder = WebApplication.CreateBuilder(args);

// DI 註冊
var dbPath = builder.Configuration["DbPath"] ?? "data/trades.db";
var tradingConfig = builder.Configuration.GetSection("TradingConfig").Get<TradingConfig>() ?? new TradingConfig();
builder.Services.AddSingleton(tradingConfig);

builder.Services.AddSingleton<MarketDataEngine>();
builder.Services.AddSingleton<IndicatorEngine>();
builder.Services.AddSingleton<IBrokerApi, MockBrokerApi>();
builder.Services.AddSingleton<StrategyBrain>(sp =>
    new StrategyBrain(
        sp.GetRequiredService<IBrokerApi>(),
        sp.GetRequiredService<IndicatorEngine>()
    ));
builder.Services.AddSingleton(_ => new TradeRepository(dbPath));

// Background services
builder.Services.AddHostedService<TradingEngine>();
builder.Services.AddHostedService<MockTickProducer>();

// CORS（Dashboard 用）
builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── API Endpoints ──────────────────────────────────────
app.MapGet("/api/trades", async (TradeRepository repo) =>
{
    var trades = await repo.GetAllTradesAsync();
    return Results.Ok(trades);
});

app.MapGet("/api/stats", async (TradeRepository repo) =>
{
    var stats = await repo.GetStatsAsync();
    return Results.Ok(stats);
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", mode = "DryRun", time = DateTime.Now }));

// Debug endpoint：注入假交易，驗證 Dashboard 顯示正確（僅 DryRun 模式可用）
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
