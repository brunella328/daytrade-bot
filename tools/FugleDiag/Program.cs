/// <summary>
/// Fugle WebSocket 診斷工具 v3
/// 單一連線，等待認證後才訂閱 trades channel，印出原始 JSON
///
/// 用法：dotnet run -- <apikey> [symbol]
/// </summary>

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var apiKey = args.Length > 0 ? args[0] : throw new Exception("Usage: dotnet run -- <apikey> [symbol]");
var symbol  = args.Length > 1 ? args[1] : "2330";

Console.WriteLine($"[Diag] Fugle WebSocket 診斷 v3");
Console.WriteLine($"[Diag] Symbol: {symbol}  ApiKey: {apiKey[..8]}...");
Console.WriteLine();

var uri = new Uri("wss://api.fugle.tw/marketdata/v1.0/stock/streaming");
using var ws  = new ClientWebSocket();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var buffer    = new byte[8192];

async Task SendJsonAsync(object payload)
{
    var json  = JsonSerializer.Serialize(payload);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
    Console.WriteLine($"[SEND] {json}");
}

async Task<string> ReceiveOnceAsync()
{
    var result = await ws.ReceiveAsync(buffer, cts.Token);
    return Encoding.UTF8.GetString(buffer, 0, result.Count);
}

string PrettyJson(string json)
{
    try
    {
        var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }
    catch { return json; }
}

try
{
    // 1. 連線
    await ws.ConnectAsync(uri, cts.Token);
    Console.WriteLine("[Diag] ✅ 已連線");

    // 2. 認證
    await SendJsonAsync(new { @event = "auth", data = new { apikey = apiKey } });

    // 3. 等待 authenticated
    var authResp = await ReceiveOnceAsync();
    Console.WriteLine($"[AUTH] {authResp}");
    if (!authResp.Contains("authenticated"))
    {
        Console.WriteLine("❌ 認證失敗，停止");
        return;
    }

    // 4. 訂閱 trades
    await SendJsonAsync(new { @event = "subscribe", data = new { channel = "trades", symbol } });

    Console.WriteLine(new string('─', 70));

    // 5. 接收訊息
    int msgCount = 0;
    while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
    {
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        if (result.MessageType == WebSocketMessageType.Close) break;

        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Console.WriteLine($"\n[MSG #{++msgCount}] {DateTime.Now:HH:mm:ss.fff}");
        Console.WriteLine(PrettyJson(json));

        if (msgCount >= 15)
        {
            Console.WriteLine("\n[Diag] 已收到 15 筆，結束");
            break;
        }
    }
}
catch (WebSocketException ex)
{
    Console.WriteLine($"\n❌ WebSocket 錯誤：{ex.Message} (code={ex.WebSocketErrorCode})");
    if (ex.InnerException != null)
        Console.WriteLine($"   InnerException: {ex.InnerException.Message}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Diag] 逾時（90 秒無訊息）");
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ {ex.GetType().Name}: {ex.Message}");
}
