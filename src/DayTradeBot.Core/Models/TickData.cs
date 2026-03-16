namespace DayTradeBot.Core.Models;

public record TickData(
    string Symbol,
    decimal Price,
    long Volume,
    DateTime Timestamp
);
