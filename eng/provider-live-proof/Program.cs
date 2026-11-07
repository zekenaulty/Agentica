using Agentica.ProviderLiveProof;

var mode = args.FirstOrDefault() ?? "self-test";
if (mode is "self-test" or "dry-run") return await ProofSelfTests.RunAsync();
if (mode is not ("serve" or "run") || !args.Contains("--live", StringComparer.Ordinal))
{
    Console.WriteLine("Default is offline self-test. Live use requires: serve|run --live --state <workspace/.agentica/path> --port <unused-port> --content-root <Agentica.Lab.Web>");
    return 2;
}
string Option(string name) => Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length
    ? args[index + 1] : throw new ArgumentException("Missing " + name);
var workspace = Path.GetFullPath(Directory.GetCurrentDirectory());
while (!File.Exists(Path.Combine(workspace, "global.json")))
    workspace = Directory.GetParent(workspace)?.FullName ?? throw new InvalidOperationException("Run from an Agentica workspace.");
var state = Path.GetFullPath(Option("--state"));
var allowed = Path.Combine(workspace, ".agentica") + Path.DirectorySeparatorChar;
if (!state.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Proof state must stay under this Agentica workspace .agentica directory.");
var port = int.Parse(Option("--port"), System.Globalization.CultureInfo.InvariantCulture);
if (port is < 1024 or > 65535 || port is 5078 or 5089) throw new InvalidOperationException("Choose a separate unused proof port.");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try { return await ProofHost.RunAsync(mode, state, port, Path.GetFullPath(Option("--content-root")), cancellation.Token); }
catch (Exception error) when (error is not OutOfMemoryException)
{
    // Provider exceptions can contain sensitive data; normal evidence lives in the ledger.
    Console.Error.WriteLine("Proof stopped: " + error.GetType().Name + ". Inspect structural ledger and disposable Lab state; no automatic retry.");
    return 1;
}
