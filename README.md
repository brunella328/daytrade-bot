# 當沖 Bot — Phase 1 (Dry Run)

全自動當沖交易系統，Phase 1 為 Dry Run 模式，所有下單走 Mock，交易紀錄存 SQLite，可透過 Dashboard 查看成效。

## 需求

- [.NET 8 SDK](https://dotnet.microsoft.com/download)

## 啟動 Dry Run

```bash
cd src/DayTradeBot.Api
dotnet run
```

預設在 `http://localhost:5000` 啟動。

## 開啟 Dashboard

瀏覽器打開：

```
http://localhost:5000
```

Dashboard 每 30 秒自動更新，顯示：
- 統計摘要：總交易數、勝率、總 PnL、平均 PnL
- 交易紀錄表格：每筆進出場明細

## API

| Endpoint | 說明 |
|----------|------|
| `GET /api/trades` | 所有交易紀錄（JSON） |
| `GET /api/stats` | 統計摘要（JSON） |
| `GET /api/health` | 健康檢查 |

## 執行測試

```bash
dotnet test
```

## 設定

`src/DayTradeBot.Api/appsettings.json`：

```json
{
  "Mode": "DryRun",
  "DbPath": "data/trades.db",
  "WatchlistPath": "watchlist.json"
}
```

## 架構

```
DayTradeBot.Core        核心引擎（Models, MarketDataEngine, IndicatorEngine, StrategyBrain）
DayTradeBot.Storage     SQLite 持久化（TradeRepository）
DayTradeBot.Api         ASP.NET Core Minimal API + Dashboard 前端
DayTradeBot.Tests       單元測試
```

## 策略說明

- **進場**：ADX(14) < 25 AND Close < BB(20,2) Lower AND RSI(14) < 30（三條件同時成立）
- **時間**：09:00–13:00 允許進場；13:00–13:30 強制清倉
- **出場**：OCO 單 — TP = 成交價 × 1.005，SL = 成交價 × 0.990
