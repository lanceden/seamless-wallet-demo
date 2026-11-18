using System.Net.Http.Json;
using System.Text.Json;
using Provider.Simulator;

SimulatorOptions options;
try { options = SimulatorOptions.Parse(args); }
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine("usage: --players N --rounds N --concurrency N --duplicate-rate R --out-of-order-rate R --rollback-rate R " +
                            "--inject-report-errors N [--base-url URL --provider CODE --secret S --out DIR --seed N --initial-balance N]");
    return 2;
}

using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = Math.Max(16, options.Concurrency * 3) };
using var http = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };

Console.WriteLine($"simulator → {options.BaseUrl}  players={options.Players} rounds={options.Rounds} concurrency={options.Concurrency} " +
                  $"dup={options.DuplicateRate} ooo={options.OutOfOrderRate} rollback={options.RollbackRate} inject={options.InjectReportErrors}");

var result = await SimulatorRunner.RunAsync(options, http);

Console.WriteLine($"done in {result.Duration.TotalSeconds:F1}s  requests={result.Requests}  retries={result.Retries}  " +
                  $"tps={result.Requests / Math.Max(result.Duration.TotalSeconds, 0.001):F0}");
foreach (var (k, v) in result.CodeCounts.OrderBy(x => x.Key)) Console.WriteLine($"  {k,-40} {v}");
Console.WriteLine($"reply mismatches: {result.ReplyMismatches}   anomalies: {result.Anomalies.Count}");
foreach (var a in result.Anomalies.Take(10)) Console.WriteLine("  ! " + a);
foreach (var f in result.ReportFiles) Console.WriteLine("report: " + f);
foreach (var e in result.InjectedErrors) Console.WriteLine($"injected: {e.DiffType} {e.TxnId}");
Console.WriteLine($"player prefix: {result.PlayerPrefix}");

var inv = await http.GetAsync("/admin/health/invariants");
var body = await inv.Content.ReadFromJsonAsync<JsonElement>();
var invariantsOk = body.GetProperty("ok").GetBoolean();
Console.WriteLine($"invariants: {(invariantsOk ? "OK" : "VIOLATED " + body.GetRawText())}");

return result.ReplyMismatches == 0 && result.Anomalies.Count == 0 && invariantsOk ? 0 : 1;
