using Dapper;
using Microsoft.Data.Sqlite;

namespace DayTradeBot.Storage;

public class TradeRepository
{
    private readonly string _connectionString;

    public TradeRepository(string dbPath = "data/trades.db")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
        InitDb();
    }

    private void InitDb()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS Trades (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Symbol      TEXT    NOT NULL,
                EntryPrice  REAL    NOT NULL,
                ExitPrice   REAL    NOT NULL,
                Qty         INTEGER NOT NULL,
                EntryTime   TEXT    NOT NULL,
                ExitTime    TEXT    NOT NULL,
                PnL         REAL    NOT NULL,
                ExitReason  TEXT    NOT NULL
            )");
    }

    public async Task InsertTradeAsync(TradeRecord trade)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO Trades (Symbol, EntryPrice, ExitPrice, Qty, EntryTime, ExitTime, PnL, ExitReason)
            VALUES (@Symbol, @EntryPrice, @ExitPrice, @Qty, @EntryTime, @ExitTime, @PnL, @ExitReason)",
            trade);
    }

    public async Task<IEnumerable<TradeRecord>> GetAllTradesAsync()
    {
        using var conn = new SqliteConnection(_connectionString);
        return await conn.QueryAsync<TradeRecord>(
            "SELECT * FROM Trades ORDER BY ExitTime DESC");
    }

    public async Task<TradeStats> GetStatsAsync()
    {
        using var conn = new SqliteConnection(_connectionString);
        var trades = (await conn.QueryAsync<TradeRecord>("SELECT * FROM Trades")).ToList();

        if (trades.Count == 0)
            return new TradeStats(0, 0, 0, 0);

        var wins = trades.Count(t => t.PnL > 0);
        var totalPnL = trades.Sum(t => t.PnL);
        var avgPnL = totalPnL / trades.Count;
        var winRate = (double)wins / trades.Count;

        return new TradeStats(trades.Count, winRate, totalPnL, avgPnL);
    }
}
