namespace DayTradeBot.Core.Models;

public class KLine
{
    public string Symbol { get; init; } = string.Empty;
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public DateTime OpenTime { get; init; }
    public DateTime CloseTime { get; set; }
    /// <summary>前一日收盤價，用於計算漲跌停。Dry Run / Mock 環境可為 null。</summary>
    public decimal? PreviousClose { get; set; }
}
