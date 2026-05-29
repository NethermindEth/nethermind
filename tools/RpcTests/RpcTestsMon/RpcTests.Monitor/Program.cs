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

    using HttpClient client = new();
    MonitorRunner runner = new(args, GetNotifier(), client);
    await runner.RunAsync(ct);
});

return await rootCommand.Parse(args).InvokeAsync();

static Uri UriParser(ArgumentResult arg)
{
    string str = arg.Tokens.Single().Value;
    return new Uri(str.Contains("://") ? str : $"http://{str}");
}

static INotifier GetNotifier()
{
    string? botToken = Environment.GetEnvironmentVariable("RPC_MONITOR_BOT_TOKEN");
    string? channelId = Environment.GetEnvironmentVariable("RPC_MONITOR_CHANNEL_ID");

    if (botToken is not null && channelId is not null)
        return new BotSlackNotifier(new BotSlackConfig { BotToken = botToken, ChannelId = channelId });

    string webhookUrl = Environment.GetEnvironmentVariable("RPC_MONITOR_WEBHOOK_URL")
        ?? throw new InvalidOperationException("Missing RPC_MONITOR_WEBHOOK_URL environment variable");

    return new WebhookSlackNotifier(webhookUrl);
}
