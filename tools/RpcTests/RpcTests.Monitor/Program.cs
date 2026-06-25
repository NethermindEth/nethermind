// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.CommandLine.Parsing;
using Nethermind.RpcTests.Monitor;
using Nethermind.RpcTests.Monitor.Notifiers;

Option<Uri> targetOption = new("--target", "-t")
{
    Description = "Testee node HTTP URL",
    CustomParser = UriParser,
    DefaultValueFactory = _ => new Uri("http://localhost:8545")
};

Option<Uri> referenceOption = new("--reference", "-r")
{
    Description = "Reference node HTTP URL",
    CustomParser = UriParser
};

Option<string[]> testsOption = new("--tests", "-g")
{
    Description = "Test file glob pattern(s), can be provided multiple times",
    AllowMultipleArgumentsPerToken = true,
    DefaultValueFactory = _ => ["mainnet/**/*"]
};

Option<string> nameOption = new("--name", "-n")
{
    Description = "Monitor instance name, defaults to glob pattern",
    AllowMultipleArgumentsPerToken = true
};

Option<int> parallelismOption = new("--parallelism", "-p")
{
    Description = "Test execution parallelism",
    DefaultValueFactory = _ => 4
};

Option<bool> devOption = new("--dev")
{
    Description = "Development mode - run without any remote notifications",
    DefaultValueFactory = _ => false
};

Option<TimeSpan?> reportAtOption = new("--report-at")
{
    Description = "UTC time of day at which to report execution statistics (e.g. 12:00:00)"
};

RootCommand rootCommand = new("Monitors a running node by periodically executing dynamic RPC tests against a reference node")
{
    targetOption,
    referenceOption,
    testsOption,
    nameOption,
    parallelismOption,
    devOption,
    reportAtOption
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    ExecutionArgs args = new()
    {
        TargetUrl = parseResult.GetRequiredValue(targetOption),
        ReferenceUrl = parseResult.GetValue(referenceOption),
        TestGlobs = parseResult.GetRequiredValue(testsOption),
        Parallelism = parseResult.GetValue(parallelismOption)
    };

    string name = parseResult.GetValue(nameOption) ?? string.Join('|', parseResult.GetRequiredValue(testsOption));
    using INotifier notifier = GetNotifier(name, parseResult.GetRequiredValue(devOption));
    using RpcClient target = new(args.TargetUrl);
    using RpcClient? reference = args.ReferenceUrl is { } referenceUrl ? new(referenceUrl) : null;
    ReorgTracker reorgTracker = new();
    BlockProvider blockProvider = new(target);

    TimeSpan? reportAt = parseResult.GetValue(reportAtOption);
    IStatsReporter stats = reportAt is { } time ? new StatsReporter(notifier, time, reorgTracker) : NullStatsReporter.Instance;
    MonitorRunner runner = new(args, notifier, stats, target, reference, reorgTracker, blockProvider);

    await Task.WhenAll(
        runner.RunAsync(ct),
        stats.RunAsync(ct)
    );

    Console.WriteLine("Monitoring finished");
});

return await rootCommand.Parse(args).InvokeAsync();

static Uri UriParser(ArgumentResult arg)
{
    string str = arg.Tokens.Single().Value;
    return new Uri(str.Contains("://") ? str : $"http://{str}");
}

static INotifier GetNotifier(string name, bool isDevelopment)
{

    if (Environment.GetEnvironmentVariable("RPC_MONITOR_BOT_TOKEN") is { } botToken &&
        Environment.GetEnvironmentVariable("RPC_MONITOR_CHANNEL_ID") is { } channelId)
    {
        return new BotSlackNotifier(name, new BotSlackConfig { BotToken = botToken, ChannelId = channelId })
            .RateLimited(10, TimeSpan.FromMinutes(30));
    }

    return isDevelopment
        ? NullNotifier.Instance
        : throw new Exception("No remote notification method configured.");
}
