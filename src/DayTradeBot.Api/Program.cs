using DayTradeBot.Core;
using DayTradeBot.Core.Broker;
using DayTradeBot.Storage;
using DayTradeBot.Api;

var builder = WebApplication.CreateBuilder(args);

// DI 註冊
var dbPath = builder.Configuration["DbPath"] ?? "data/trades.db";

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

app.Run();
