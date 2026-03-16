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
    private const int MinRequiredBars = 30;

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
