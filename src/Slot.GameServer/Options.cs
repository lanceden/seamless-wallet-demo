namespace Slot.GameServer;

public sealed class WalletOptions
{
    public const string Section = "Wallet";
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string ProviderCode { get; set; } = "SLOT";
    /// <summary>與錢包共用的遊戲商 secret：簽章呼叫錢包，也驗證平台來的 launch 請求。</summary>
    public string Secret { get; set; } = "slot-secret";
    /// <summary>對同一個請求（同 txnId）最多嘗試幾次；全部失敗才算「結果未知」。</summary>
    public int MaxAttempts { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 200;
    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class GameOptions
{
    public const string Section = "Game";
    /// <summary>可選的每線下注（分）。總下注 = 每線下注 × 10。</summary>
    public long[] LineBets { get; set; } = [100, 200, 500, 1000, 2000, 5000];
    public long DefaultLineBet { get; set; } = 100;
    public string GameCode { get; set; } = "SLOT01";
    public string Currency { get; set; } = "TWD";
    public string ClientUrl { get; set; } = "http://localhost:8081/";
    public int TokenHours { get; set; } = 2;
}

public sealed class RecoveryOptions
{
    public const string Section = "Recovery";
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 10;
    /// <summary>SETTLING 超過幾秒沒動靜就由 Worker 補派彩（計畫：30 秒）。</summary>
    public int StaleSettlingSeconds { get; set; } = 30;
    /// <summary>BETTING 超過幾秒沒動靜就由 Worker 取消（要大於一局正常最長耗時）。</summary>
    public int StaleBettingSeconds { get; set; } = 60;
}

public sealed class ReportOptions
{
    public const string Section = "Report";
    public string Directory { get; set; } = "reports";
}
