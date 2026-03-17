using System.Runtime.InteropServices;
using DayTradeBot.Capital.Interfaces;

namespace DayTradeBot.Capital;

/// <summary>
/// 群益 SKOrderLib 下單封裝。
///
/// 下單流程：
/// 1. PlaceMarketBuyAsync → SendStockOrder（市價買進）
/// 2. 監聽 OnTradeReport → 取得成交均價
/// 3. PlaceOcoOrderAsync → 送出主機端二擇一條件單（停利 MIT + 停損）
///
/// ⚠️  群益主機端智慧單：一邊成交後，另一邊由主機自動取消，不需 C# 端盯盤。
/// </summary>
public class SKOrderWrapper : IOrderService, IDisposable
{
    public event EventHandler<TradeReportArgs>? OnTradeReport;
    public bool IsConnected { get; private set; }

    private dynamic? _skOrder;
    private readonly CapitalApiManager _center;
    private readonly string _account;

    // 追蹤送出的委託單（OrderId → Symbol）
    private readonly Dictionary<string, string> _pendingOrders = new();

    public SKOrderWrapper(CapitalApiManager center, string account)
    {
        _center = center;
        _account = account;
    }

    public void Connect()
    {
        if (!_center.IsLoggedIn)
            throw new InvalidOperationException("請先完成 SKCenterLib 登入");

        var type = Type.GetTypeFromProgID("SKOrderLib.SKOrderLib")
            ?? throw new InvalidOperationException("找不到 SKOrderLib COM 元件");

        _skOrder = Activator.CreateInstance(type);

        int rc = _skOrder.SKOrderLib_Initialize(_account);
        if (rc != 0)
            throw new InvalidOperationException($"SKOrderLib 初始化失敗，code={rc}");

        // 綁定成交回報事件
        ((dynamic)_skOrder).OnTradeReport += new Action<string, string>(OnTradeReportHandler);

        IsConnected = true;
        Console.WriteLine("[Capital] SKOrderLib 連線成功");
    }

    /// <summary>
    /// 現貨市價買進。
    /// 群益 SendStockOrder 參數說明：
    ///   bstrFullAccount：完整帳號（含分公司代號）
    ///   bstrStockNo：股票代號
    ///   nBSFlag：0=買進
    ///   nTradeType：0=現股
    ///   nDayTrade：1=當沖（必須設 1 才允許當日回補）
    ///   nQty：張數（1 張 = 1000 股）
    ///   nPrice：0 = 市價
    ///   nPriceType：1=市價單
    /// </summary>
    public async Task<OrderResult> PlaceMarketBuyAsync(string symbol, long qty)
    {
        if (_skOrder is null) return new OrderResult(false, "", "SKOrderLib 未初始化");

        var orderId = Guid.NewGuid().ToString("N")[..8];
        _pendingOrders[orderId] = symbol;

        try
        {
            // 群益下單 API（Late Binding）
            // 實際參數需依群益 API 文件的 SendStockOrder 簽名調整
            int rc = _skOrder.SKOrderLib_SendStockOrder(
                _account,   // bstrFullAccount
                symbol,     // bstrStockNo
                0,          // nBSFlag: 0=買進
                0,          // nTradeType: 0=現股
                1,          // nDayTrade: 1=當沖
                (int)qty,   // nQty
                0,          // nPrice: 0=市價
                1           // nPriceType: 1=市價
            );

            if (rc != 0)
            {
                _pendingOrders.Remove(orderId);
                return new OrderResult(false, "", $"下單失敗 code={rc}");
            }

            Console.WriteLine($"[Capital] 買進送出 {symbol} qty={qty} orderId={orderId}");
            return new OrderResult(true, orderId, "委託送出");
        }
        catch (Exception ex)
        {
            _pendingOrders.Remove(orderId);
            return new OrderResult(false, "", ex.Message);
        }
    }

    /// <summary>
    /// 送出主機端 OCO 二擇一條件單。
    /// 群益現貨智慧單：停利用 MIT（Market if Touched），停損用條件單。
    /// 一邊觸發成交後，主機自動取消另一邊。
    ///
    /// ⚠️  實際 API 函式名稱與參數請依群益最新文件確認（版本差異大）。
    ///     常見函式：SKOrderLib_SendSmartConditionOrder 或分開送兩筆條件單。
    /// </summary>
    public async Task<OrderResult> PlaceOcoOrderAsync(OcoParams order)
    {
        if (_skOrder is null) return new OrderResult(false, "", "SKOrderLib 未初始化");

        try
        {
            // 停利單：MIT（到價市價賣出）
            int rcTp = _skOrder.SKOrderLib_SendSmartStockOrder(
                _account,
                order.Symbol,
                1,                              // nBSFlag: 1=賣出
                0,                              // nTradeType: 0=現股
                1,                              // nDayTrade: 1=當沖
                (int)order.Qty,
                (int)(order.TakeProfitPrice * 1000),  // 群益價格單位 × 1000
                2,                              // nPriceType: 2=MIT
                0                               // nCondition: 0=>=（觸價向上）
            );

            // 停損單：條件單（跌破停損價市價賣出）
            int rcSl = _skOrder.SKOrderLib_SendSmartStockOrder(
                _account,
                order.Symbol,
                1,
                0,
                1,
                (int)order.Qty,
                (int)(order.StopLossPrice * 1000),
                2,
                1                               // nCondition: 1=<=（觸價向下）
            );

            var ok = rcTp == 0 && rcSl == 0;
            Console.WriteLine($"[Capital] OCO {order.Symbol} TP={order.TakeProfitPrice} SL={order.StopLossPrice} rc=({rcTp},{rcSl})");
            return new OrderResult(ok, "", ok ? "OCO 送出" : $"OCO 失敗 tp_rc={rcTp} sl_rc={rcSl}");
        }
        catch (Exception ex)
        {
            return new OrderResult(false, "", ex.Message);
        }
    }

    public async Task<bool> CancelOrderAsync(string orderId)
    {
        if (_skOrder is null) return false;
        try
        {
            // SKOrderLib_CancelOrder：取消委託
            int rc = _skOrder.SKOrderLib_CancelOrder(_account, orderId);
            Console.WriteLine($"[Capital] 取消委託 {orderId} rc={rc}");
            return rc == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 成交回報事件處理。
    /// bstrData：群益回傳的成交資料字串（Big5，逗號分隔）
    /// 欄位順序依群益文件，常見：帳號,股票代號,買賣,成交均價,成交量,委託書號,...
    /// </summary>
    private void OnTradeReportHandler(string bstrUserData, string bstrData)
    {
        try
        {
            var decoded = CapitalApiManager.DecodeBig5(bstrData);
            var fields = decoded.Split(',');

            // 依群益 OnReport 欄位解析（欄位順序請依實際文件驗證）
            var symbol    = fields.ElementAtOrDefault(1)?.Trim() ?? "";
            var bsFlag    = fields.ElementAtOrDefault(2)?.Trim(); // "B"=買 "S"=賣
            var fillPrice = decimal.TryParse(fields.ElementAtOrDefault(4), out var fp) ? fp : 0m;
            var fillQty   = long.TryParse(fields.ElementAtOrDefault(5), out var fq) ? fq : 0L;
            var orderId   = fields.ElementAtOrDefault(7)?.Trim() ?? "";

            // 群益價格單位是整數，需除以 1000
            if (fillPrice > 1000) fillPrice /= 1000m;

            var orderType = bsFlag == "B" ? "BUY" : "SELL";
            var report = new TradeReportArgs(orderId, symbol, fillPrice, fillQty, DateTime.Now, orderType);
            OnTradeReport?.Invoke(this, report);

            Console.WriteLine($"[Capital] 成交回報 {symbol} {orderType} @ {fillPrice} qty={fillQty}");
        }
        catch
        {
            // 回呼中靜默吸收，不可拋出
        }
    }

    public void Dispose()
    {
        if (_skOrder is not null)
        {
            try { Marshal.ReleaseComObject(_skOrder); } catch { }
            _skOrder = null;
        }
        IsConnected = false;
    }
}
