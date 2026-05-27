// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.CommandLine.Parsing;
using RpcTestsMon;

static Uri UriParser(ArgumentResult arg)
{
    string str = arg.Tokens.Single().Value;
    return new Uri(str.Contains("://") ? str : $"http://{str}");
}

Option<Uri> targetOption = new("--target", "-t")
{
    Description = "Testee node HTTP URL",
    CustomParser = UriParser,
    DefaultValueFactory = _ => new Uri("http://localhost:8545")
};

Option<Uri> referenceOption = new("--reference", "-r")
{
    Description = "Reference node HTTP URL",
    CustomParser = UriParser,
    Required = true
};

Option<string[]> testsOption = new("--tests", "-g")
{
    Description = "Test file glob pattern(s), can be provided multiple times",
    AllowMultipleArgumentsPerToken = true,
    DefaultValueFactory = _ => ["mainnet/**/*"]
};

Option<int> parallelismOption = new("--parallelism", "-p")
{
    Description = "Test execution parallelism",
    DefaultValueFactory = _ => 4
};

RootCommand rootCommand = new("Monitors a running node by periodically executing dynamic RPC tests against a reference node")
{
    targetOption,
    referenceOption,
    testsOption,
    parallelismOption
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    ExecutionArgs args = new()
    {
        TargetUrl = parseResult.GetRequiredValue(targetOption),
        ReferenceUrl = parseResult.GetRequiredValue(referenceOption),
        TestGlobs = parseResult.GetRequiredValue(testsOption),
        Parallelism = parseResult.GetValue(parallelismOption)
    };

    const string webhookEnvName = "RPC_TESTS_MONITOR_WEBHOOK_URL";
    string webhookUrl = Environment.GetEnvironmentVariable(webhookEnvName)
        ?? throw new InvalidOperationException($"Missing {webhookEnvName} environment variable");
    SlackNotifier notifier = new(webhookUrl);

    MonitorRunner runner = new(notifier);
    await runner.RunAsync(args, ct);
});

return await rootCommand.Parse(args).InvokeAsync();
