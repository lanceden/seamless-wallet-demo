using System.Diagnostics;
using Slot.Core;
using Slot.RtpSimulator;

// 用法：
//   dotnet run --project src/Slot.RtpSimulator -c Release -- --spins 10000000 --seed-free
//   --spins N      蒙地卡羅局數（預設 10,000,000）
//   --seed N       可重現的模擬（DeterministicRandomSource）；預設用 --seed-free
//   --seed-free    使用正式的密碼學亂數（RandomNumberGenerator），不可重現（預設）
//   --no-exact     跳過完整週期列舉
//   --tune         先用退火搜尋調整轉輪帶格數與賠率表，再對結果做報表（印出可貼進 SlotConfig.Default 的內容）
//     --target-rtp 0.96  --target-hit 0.30  --iterations 3000  --tune-seed 1
long spins = 10_000_000; ulong? seed = null; var exact = true;
var tune = false; double targetRtp = 0.96, targetHit = 0.30; var iterations = 3000; var tuneSeed = 1;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--spins": spins = long.Parse(args[++i]); break;
        case "--seed": seed = ulong.Parse(args[++i]); break;
        case "--seed-free": seed = null; break;
        case "--no-exact": exact = false; break;
        case "--tune": tune = true; break;
        case "--target-rtp": targetRtp = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--target-hit": targetHit = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--iterations": iterations = int.Parse(args[++i]); break;
        case "--tune-seed": tuneSeed = int.Parse(args[++i]); break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

var config = SlotConfig.Default;
config.Validate();
if (tune)
{
    var sw0 = Stopwatch.StartNew();
    var t = Tuner.Tune(config, targetRtp, targetHit, iterations, tuneSeed);
    config = t.Config;
    Console.WriteLine($"== 調參完成（{iterations} 次迭代，{sw0.Elapsed.TotalSeconds:F0}s，seed {tuneSeed}）：解析 RTP {t.Rtp:P3}，估計中獎率 {t.HitRate:P1} ==");
    Console.WriteLine();
    Console.WriteLine("// 貼進 SlotConfig.Default 的轉輪帶：");
    foreach (var strip in config.Strips) Console.WriteLine($"    \"{StripBuilder.ToLiteral(strip)}\",");
    Console.WriteLine();
    Console.WriteLine("// 貼進 PayTable.Default 的賠率（3 / 4 / 5 連，× 每線下注）：");
    foreach (var (sym, m) in config.Pay.Entries.OrderBy(e => e.Key == Symbol.Seven ? 0 : (int)e.Key))
        Console.WriteLine($"    [Symbol.{sym}] = [{string.Join(", ", m)}],");
    Console.WriteLine();
}
Console.WriteLine($"轉輪帶長度：{string.Join(" × ", config.Strips.Select(s => s.Length))} = {RtpCalculator.CycleLength(config):N0} 種停止組合");
Console.WriteLine($"解析式 RTP（只看各軸符號數量）：{RtpCalculator.Analytic(config):P4}");

RtpReport? ex = null;
if (exact)
{
    var sw = Stopwatch.StartNew();
    ex = RtpCalculator.Exact(config);
    Console.WriteLine($"\n== 完整週期列舉（{sw.Elapsed.TotalSeconds:F1}s，無抽樣誤差）==");
    Console.WriteLine($"RTP        {ex.Rtp:P4}");
    Console.WriteLine($"中獎率     {ex.HitRate:P2}");
    Console.WriteLine($"最大單局   {ex.MaxWinMultiplier:N0}× 總下注");
    Console.WriteLine("\n各符號 × 連數對 RTP 的貢獻：");
    foreach (var (k, v) in ex.Contributions.OrderByDescending(x => x.Value))
        Console.WriteLine($"  {SymbolCodes.ToCode(k.Symbol),-7} ×{k.Count}   {v,8:P3}   ({v / ex.Rtp,6:P1} of RTP)");
}

{
    var sw = Stopwatch.StartNew();
    var sim = seed is { } s ? RtpSimulation.Run(config, spins, s) : RtpSimulation.RunSecure(config, spins);
    Console.WriteLine($"\n== 蒙地卡羅 {spins:N0} 局（{(seed is null ? "SecureRandomSource" : $"seed {seed}")}，{sw.Elapsed.TotalSeconds:F1}s）==");
    Console.WriteLine($"RTP        {sim.Rtp:P4}   ±{1.96 * sim.StdErr:P3}（95% 信賴區間）");
    Console.WriteLine($"中獎率     {sim.HitRate:P2}");
    Console.WriteLine($"最大單局   {sim.MaxWinMultiplier:N0}× 總下注");
    Console.WriteLine($"大獎（≥20×）{sim.BigWins:N0} 局，約每 {(sim.BigWins == 0 ? double.PositiveInfinity : (double)sim.Spins / sim.BigWins):N0} 局一次");
    Console.WriteLine($"每局報酬標準差 {sim.StdErr * Math.Sqrt(sim.Spins):F2}（單位：倍總下注）");
    if (ex is not null)
        Console.WriteLine($"與列舉值的差 {sim.Rtp - ex.Rtp:+0.0000%;-0.0000%}（{(Math.Abs(sim.Rtp - ex.Rtp) <= 3 * sim.StdErr ? "在 3σ 內 ✓" : "超出 3σ ✗")}）");
}
return 0;
