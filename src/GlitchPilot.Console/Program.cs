using GlitchPilot.Core;
using GlitchPilot.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

DotNetEnv.Env.Load();

var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logDir);
var logFile = Path.Combine(logDir, $"glitchpilot-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

var app = Host.CreateApplicationBuilder(args);
app.Services.AddGlitchPilot();
app.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
app.Services.AddSingleton<ILoggerProvider>(new FileLoggerProvider(logFile));

using var host = app.Build();

var mode = ResolveMode(args);
using var cts = new CancellationTokenSource();

return mode switch
{
    "triage" => await RunAsync<TriagePipeline>(host, cts.Token),
    "verify" => await RunAsync<VerifyPipeline>(host, cts.Token),
    "probe"  => await RunProbeAsync(host, args, cts.Token),
    _ => Error($"Unknown mode '{mode}'. Use --mode triage, verify, or probe."),
};

static async Task<int> RunAsync<T>(IHost host, CancellationToken ct) where T : class
{
    var pipeline = host.Services.GetRequiredService<T>();
    await ((dynamic)pipeline).RunAsync(ct);
    return 0;
}

static async Task<int> RunProbeAsync(IHost host, string[] args, CancellationToken ct)
{
    var issueId = ResolveArg(args, "--issue");
    if (issueId is null)
        return Error("Probe mode requires --issue <id>. Example: --mode probe --issue 22");

    var fileIssue = args.Contains("--file");
    var sendEmail = args.Contains("--email");
    var pipeline = host.Services.GetRequiredService<ProbePipeline>();
    await pipeline.RunAsync(issueId, fileIssue, sendEmail, ct);
    return 0;
}

static string ResolveMode(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--mode" && i + 1 < args.Length)
            return args[i + 1].ToLowerInvariant();
        if (args[i].StartsWith("--mode="))
            return args[i]["--mode=".Length..].ToLowerInvariant();
    }
    return "triage";
}

static string? ResolveArg(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == name && i + 1 < args.Length)
            return args[i + 1];
        if (args[i].StartsWith($"{name}="))
            return args[i][$"{name}=".Length..];
    }
    return null;
}

static int Error(string msg) { Console.Error.WriteLine(msg); return 1; }
