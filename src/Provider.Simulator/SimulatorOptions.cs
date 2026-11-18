using System.Globalization;

namespace Provider.Simulator;

public sealed class SimulatorOptions
{
    public string BaseUrl { get; init; } = "http://localhost:8080";
    public string ProviderCode { get; init; } = "DEMO";
    public string Secret { get; init; } = "demo-secret";
    public int Players { get; init; } = 1000;
    public int Rounds { get; init; } = 50_000;
    public int Concurrency { get; init; } = 64;
    public double DuplicateRate { get; init; } = 0.05;
    public double OutOfOrderRate { get; init; } = 0.02;
    public double RollbackRate { get; init; } = 0.03;
    public int InjectReportErrors { get; init; }
    public string OutDir { get; init; } = "reports";
    public int Seed { get; init; } = 20260101;
    /// <summary>每位玩家的起始餘額（分）。</summary>
    public long InitialBalance { get; init; } = 5_000_000;
    /// <summary>單次請求逾時秒數；逾時視同 INTERNAL_ERROR，以同一個 txnId 重送。</summary>
    public int RequestTimeoutSeconds { get; init; } = 10;
    /// <summary>單筆請求最長重試秒數（API 被 kill / 資料庫重啟時用來撐過去）。</summary>
    public int MaxRetrySeconds { get; init; } = 120;

    public static SimulatorOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"unexpected argument '{args[i]}'");
            if (i + 1 >= args.Length) throw new ArgumentException($"missing value for {args[i]}");
            map[args[i][2..]] = args[i + 1];
        }

        string Str(string k, string d) => map.Remove(k, out var v) ? v : d;
        int Int(string k, int d, int min)
        {
            if (!map.Remove(k, out var v)) return d;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < min)
                throw new ArgumentException($"--{k} must be an integer >= {min}");
            return n;
        }
        long Long(string k, long d, long min)
        {
            if (!map.Remove(k, out var v)) return d;
            if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < min)
                throw new ArgumentException($"--{k} must be an integer >= {min}");
            return n;
        }
        double Rate(string k, double d)
        {
            if (!map.Remove(k, out var v)) return d;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0 || n > 1)
                throw new ArgumentException($"--{k} must be a number in [0,1]");
            return n;
        }

        var provider = map.TryGetValue("provider", out var pv) ? pv : "DEMO";
        var o = new SimulatorOptions
        {
            BaseUrl = Str("base-url", "http://localhost:8080"),
            ProviderCode = Str("provider", "DEMO"),
            Secret = Str("secret", "demo-secret"),
            Players = Int("players", 1000, 1),
            Rounds = Int("rounds", 50_000, 1),
            Concurrency = Int("concurrency", 64, 1),
            DuplicateRate = Rate("duplicate-rate", 0.05),
            OutOfOrderRate = Rate("out-of-order-rate", 0.02),
            RollbackRate = Rate("rollback-rate", 0.03),
            InjectReportErrors = Int("inject-report-errors", 0, 0),
            OutDir = map.ContainsKey("out") ? Str("out", "") : Path.Combine("reports", provider),
            Seed = Int("seed", 20260101, 0),
            InitialBalance = Long("initial-balance", 5_000_000, 1),
            RequestTimeoutSeconds = Int("timeout-seconds", 10, 1),
            MaxRetrySeconds = Int("max-retry-seconds", 120, 1),
        };
        if (map.Count > 0) throw new ArgumentException($"unknown option --{map.Keys.First()}");
        return o;
    }
}
