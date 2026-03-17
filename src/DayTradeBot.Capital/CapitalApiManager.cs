using System.Runtime.InteropServices;
using System.Text;

namespace DayTradeBot.Capital;

/// <summary>
/// 群益 API 連線中樞。負責：
/// 1. Big5 編碼註冊（必須在所有群益呼叫前執行）
/// 2. SKCenterLib 登入（必須成功後才能呼叫報價/下單）
/// 3. STA Thread 環境確認
///
/// ⚠️  此類別所有方法必須在 STA Thread 上呼叫。
///     在 Program.cs 的主執行緒加上 [STAThread]，
///     或在 BackgroundService 中使用 Thread 物件並設定 ApartmentState.STA。
/// </summary>
public class CapitalApiManager : IDisposable
{
    private dynamic? _skCenter;
    private bool _loggedIn;

    public bool IsLoggedIn => _loggedIn;

    /// <summary>
    /// 初始化群益環境：
    /// - 註冊 Big5 編碼（.NET 6+ 預設不含 CodePages）
    /// - 建立 SKCenterLib COM 物件
    /// 必須在 STA Thread 上呼叫。
    /// </summary>
    public void Initialize()
    {
        // Big5 修正：.NET 6+ 必須手動註冊 CodePages
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("群益 Capital API 僅支援 Windows x64 環境");

        // 建立 SKCenterLib COM 物件（Late Binding，不需要預先加入 COM 參考）
        var type = Type.GetTypeFromProgID("SKCenterLib.SKCenterLib")
            ?? throw new InvalidOperationException(
                "找不到 SKCenterLib COM 元件。請確認已安裝群益 API 並完成 DLL 註冊。");

        _skCenter = Activator.CreateInstance(type);
    }

    /// <summary>
    /// 執行群益帳號登入。
    /// 回傳 true 代表登入成功（ReturnCode = 0）。
    /// </summary>
    public bool Login(string account, string password)
    {
        if (_skCenter is null)
            throw new InvalidOperationException("請先呼叫 Initialize()");

        int returnCode = _skCenter.SKCenterLib_Login(account, password);

        if (returnCode == 0)
        {
            _loggedIn = true;
            Console.WriteLine("[Capital] 登入成功");
            return true;
        }

        var msg = (string)_skCenter.SKCenterLib_GetReturnCodeMessage(returnCode);
        Console.WriteLine($"[Capital] 登入失敗 code={returnCode} msg={DecodeBig5(msg)}");
        return false;
    }

    /// <summary>將 Big5 byte[] 或字串轉換為 UTF-8 字串</summary>
    public static string DecodeBig5(string raw)
    {
        try
        {
            var big5 = Encoding.GetEncoding("big5");
            var bytes = Encoding.Latin1.GetBytes(raw);
            return big5.GetString(bytes);
        }
        catch
        {
            return raw;
        }
    }

    public void Dispose()
    {
        if (_skCenter is not null)
        {
            try { Marshal.ReleaseComObject(_skCenter); } catch { }
            _skCenter = null;
        }
    }
}
