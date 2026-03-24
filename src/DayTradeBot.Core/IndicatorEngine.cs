using DayTradeBot.Core.Models;
using Skender.Stock.Indicators;

namespace DayTradeBot.Core;

public record IndicatorResult(
    double? Adx,
    double? BbLower,
    double? Rsi
);

public class IndicatorEngine
{
    // P3-2：MinRequiredBars 降為 15，讓重啟後約 15 分鐘就能通過 null guard 進入指標計算。
    // 注意：BB(20) 需要至少 20 根才能產生有效 LowerBand；第 15-19 根期間 bb 仍為 null，
    // UseBbCondition=true 時 bbOk=false，Triple Confirmation 不會觸發進場，行為正確。
    // 實際進場最早仍需等 BB 有值（第 20 根），但避免了不必要的 early-exit overhead。
    private const int MinRequiredBars = 15;

    /// <summary>
    /// 從 K線陣列計算最新一根的 ADX(14)、BB(20,2) LowerBand、RSI(14)。
    /// K線數量不足 MinRequiredBars 時回傳 null。
    /// </summary>
    public virtual IndicatorResult? Calculate(IReadOnlyList<KLine> klines)
    {
        if (klines.Count < MinRequiredBars) return null;

        var quotes = klines
            .Select(k => new Quote
            {
                Date = k.OpenTime,
                Open = k.Open,
                High = k.High,
                Low = k.Low,
                Close = k.Close,
                Volume = k.Volume
            })
            .ToList();

        var adx = quotes.GetAdx(14).LastOrDefault()?.Adx;
        var bb = quotes.GetBollingerBands(20, 2).LastOrDefault()?.LowerBand;
        var rsi = quotes.GetRsi(14).LastOrDefault()?.Rsi;

        return new IndicatorResult(adx, bb, rsi);
    }
}
