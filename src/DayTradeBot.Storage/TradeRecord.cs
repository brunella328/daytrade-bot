namespace DayTradeBot.Storage;

public class TradeRecord
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public long Qty { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime ExitTime { get; set; }
    public decimal GrossPnL { get; set; }      // 稅前損益
    public decimal Commission { get; set; }    // 手續費（買 + 賣）
    public decimal Tax { get; set; }           // 交易稅（賣出 0.3%）
    public decimal NetPnL { get; set; }        // 稅後淨損益 = GrossPnL - Commission - Tax
    public string ExitReason { get; set; } = string.Empty; // TP | SL | ForceClose

    // 向下相容（舊 Dashboard 欄位）
    public decimal PnL => NetPnL;
}

public record TradeStats(
    int TotalTrades,
    double WinRate,
    decimal TotalPnL,     // 稅後
    decimal AvgPnL        // 稅後
);
