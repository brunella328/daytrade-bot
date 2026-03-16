namespace DayTradeBot.Core.Models;

public record Signal(
    string Symbol,
    decimal EntryPrice,
    DateTime Timestamp
);
