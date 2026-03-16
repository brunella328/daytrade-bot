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
    public decimal PnL { get; set; }
    public string ExitReason { get; set; } = string.Empty; // TP | SL | ForceClose
}

public record TradeStats(
    int TotalTrades,
    double WinRate,
    decimal TotalPnL,
    decimal AvgPnL
);
