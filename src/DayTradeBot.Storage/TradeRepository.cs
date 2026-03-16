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
                GrossPnL    REAL    NOT NULL DEFAULT 0,
                Commission  REAL    NOT NULL DEFAULT 0,
                Tax         REAL    NOT NULL DEFAULT 0,
                NetPnL      REAL    NOT NULL DEFAULT 0,
                ExitReason  TEXT    NOT NULL
            )");

        // 舊版 schema 升級（若已有 PnL 欄位但無 NetPnL）
        try { conn.Execute("ALTER TABLE Trades ADD COLUMN GrossPnL REAL NOT NULL DEFAULT 0"); } catch { }
        try { conn.Execute("ALTER TABLE Trades ADD COLUMN Commission REAL NOT NULL DEFAULT 0"); } catch { }
        try { conn.Execute("ALTER TABLE Trades ADD COLUMN Tax REAL NOT NULL DEFAULT 0"); } catch { }
        try { conn.Execute("ALTER TABLE Trades ADD COLUMN NetPnL REAL NOT NULL DEFAULT 0"); } catch { }
    }

    public async Task InsertTradeAsync(TradeRecord trade)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO Trades (Symbol, EntryPrice, ExitPrice, Qty, EntryTime, ExitTime,
                                GrossPnL, Commission, Tax, NetPnL, ExitReason)
            VALUES (@Symbol, @EntryPrice, @ExitPrice, @Qty, @EntryTime, @ExitTime,
                    @GrossPnL, @Commission, @Tax, @NetPnL, @ExitReason)",
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

        var wins = trades.Count(t => t.NetPnL > 0);
        var totalPnL = trades.Sum(t => t.NetPnL);
        var avgPnL = totalPnL / trades.Count;
        var winRate = (double)wins / trades.Count;

        return new TradeStats(trades.Count, winRate, totalPnL, avgPnL);
    }
}
