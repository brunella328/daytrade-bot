using System.Runtime.InteropServices;
using DayTradeBot.Capital.Interfaces;
using DayTradeBot.Core.Models;

namespace DayTradeBot.Capital;

/// <summary>
/// 群益 SKQuoteLib 報價封裝。
///
/// ⚠️  鐵律：OnNotifyTicks 回呼中只做最輕量的資料轉換 + Enqueue，
///     禁止在此做任何計算，立刻把執行緒還給群益，否則報價堵塞/斷線。
///
/// ⚠️  必須在 STA Thread 上初始化（COM 元件要求）。
/// </summary>
public class SKQuoteWrapper : IQuoteService, IDisposable
{
    public event EventHandler<TickData>? OnTickReceived;
    public bool IsConnected { get; private set; }

    private dynamic? _skQuote;
    private readonly CapitalApiManager _center;

    public SKQuoteWrapper(CapitalApiManager center)
    {
        _center = center;
    }

    /// <summary>建立 SKQuoteLib COM 物件並連線</summary>
    public void Connect()
    {
        if (!_center.IsLoggedIn)
            throw new InvalidOperationException("請先完成 SKCenterLib 登入");

        var type = Type.GetTypeFromProgID("SKQuoteLib.SKQuoteLib")
            ?? throw new InvalidOperationException("找不到 SKQuoteLib COM 元件");

        _skQuote = Activator.CreateInstance(type);

        // 連線至群益報價伺服器
        int rc = _skQuote.SKQuoteLib_EnterMonitor();
        if (rc != 0)
            throw new InvalidOperationException($"SKQuoteLib 連線失敗，code={rc}");

        // ── 事件綁定 ─────────────────────────────────────────────────
        // 訂閱 OnNotifyTicks：每筆新成交 Tick 觸發
        // 注意：群益事件用 += 動態綁定（COM event）
        ((dynamic)_skQuote).OnNotifyTicks += new Action<int, int, int, int, int, int, int, int, int>(
            OnNotifyTicksHandler);

        IsConnected = true;
        Console.WriteLine("[Capital] SKQuoteLib 連線成功");
    }

    /// <summary>訂閱 watchlist 股票，開始接收 Tick</summary>
    public Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        if (_skQuote is null) throw new InvalidOperationException("請先呼叫 Connect()");

        foreach (var symbol in symbols)
        {
            // SKQuoteLib_RequestStocks：訂閱個股報價
            // 參數：股票代號字串（多檔用逗號分隔）
            int rc = _skQuote.SKQuoteLib_RequestStocks(ref ct, symbol);
            if (rc != 0)
                Console.WriteLine($"[Capital] 訂閱 {symbol} 失敗 code={rc}");
            else
                Console.WriteLine($"[Capital] 已訂閱：{symbol}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 群益 OnNotifyTicks 事件回呼。
    /// 參數依序：StockIdx, Ptr, Date, TimeSHHMMSS, MillSec, BestBid, BestAsk, Close, Qty
    ///
    /// ⚠️  此方法必須極速執行，只做 Enqueue，其餘全禁止。
    /// </summary>
    private void OnNotifyTicksHandler(
        int stockIdx, int ptr, int date, int timeSHHMMSS,
        int millSec, int bestBid, int bestAsk, int close, int qty)
    {
        try
        {
            // 群益價格單位是「整數 × 1000」（例：780000 = 780.000 元）
            var price = close / 1000m;
            var volume = (long)qty;

            // 解析時間（格式：HHMMSS）
            var h = timeSHHMMSS / 10000;
            var m = timeSHHMMSS % 10000 / 100;
            var s = timeSHHMMSS % 100;
            var now = DateTime.Today.AddHours(h).AddMinutes(m).AddSeconds(s);

            // 取得股票代號（透過 SKQuoteLib_GetStockByIndex）
            var symbol = GetSymbolByIndex(stockIdx);

            // ── 唯一動作：觸發事件，讓 MarketDataEngine 的訂閱者處理 ──
            OnTickReceived?.Invoke(this, new TickData(symbol, price, volume, now));
        }
        catch
        {
            // 回呼中不可拋出例外，靜默吸收
        }
    }

    private string GetSymbolByIndex(int idx)
    {
        if (_skQuote is null) return idx.ToString();
        try
        {
            // SKQuoteLib_GetStockByIndex 回傳 BSTR（Big5 編碼的股票代號）
            string raw = _skQuote.SKQuoteLib_GetStockByIndex(0, idx);
            return CapitalApiManager.DecodeBig5(raw).Split(',')[0].Trim(); // 第一欄是股票代號
        }
        catch { return idx.ToString(); }
    }

    public void Dispose()
    {
        if (_skQuote is not null)
        {
            try { _skQuote.SKQuoteLib_LeaveMonitor(); } catch { }
            try { Marshal.ReleaseComObject(_skQuote); } catch { }
            _skQuote = null;
        }
        IsConnected = false;
    }
}
