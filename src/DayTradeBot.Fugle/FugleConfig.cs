namespace DayTradeBot.Fugle;

public class FugleConfig
{
    public string ApiKey { get; set; } = "";

    /// <summary>EdDSA 私鑰路徑（.pem），交易 API 簽章用</summary>
    public string PrivateKeyPath { get; set; } = "";
}
