// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.MetricsConsumer;
using System.CommandLine;

namespace Nethermind.Tools.Kute;

public static class Config
{
    public static Option<string> MessagesFilePath { get; } = new("--input", "-i")
    {
        Description = "Path to a file or directory containing JSON RPC messages",
        HelpName = "path",
        Required = true,
    };

    public static Option<string> HostAddress { get; } = new("--address", "-a")
    {
        DefaultValueFactory = r => "http://localhost:8551",
        Description = "Address where to send JSON RPC requests",
        HelpName = "URL"
    };

    public static Option<string> JwtSecretFilePath { get; } = new("--secret", "-s")
    {
        Description = "Path to file with hex-encoded secret for JWT authentication",
        HelpName = "value",
        Required = true
    };

    public static Option<int> AuthTtl { get; } = new("--ttl", "-t")
    {
        DefaultValueFactory = r => 60,
        Description = "Authentication time to live (TTL), in seconds",
        HelpName = "value"
    };

    public static Option<bool> DryRun { get; } = new("--dry", "-d")
    {
        Description = "Only log into console"
    };

    public static Option<bool> ShowProgress { get; } = new("--progress", "-p")
    {
        Description = "Show progress"
    };

    public static Option<MetricsOutputFormatter> MetricsOutputFormatter { get; } = new("--output", "-o")
    {
        DefaultValueFactory = r => MetricsConsumer.MetricsOutputFormatter.Report,
        Description = "Strategy to report metrics",
        HelpName = "value",
    };

    public static Option<IEnumerable<string>> MethodFilters { get; } = new("--filters", "-f")
    {
        DefaultValueFactory = r => [],
        CustomParser = r => r.Tokens.Count == 1 ? r.Tokens[0].Value.Split(',') : null,
        Description = "A comma separated List of regexes of methods to be executed with optional limits",
        HelpName = "value",
    };

    public static Option<string> ResponsesTraceFile { get; } = new("--responses", "-r")
    {
        Description = "Path to file to store JSON-RPC responses",
        HelpName = "path"
    };

    public static Option<int> RequestsPerSecond { get; } = new("--rps", "-e")
    {
        Description = "If set to higher than 0, then requests will be send in selected RPS (Requests per seconds) rate. If 0 (or lower) then requests will be sent sequentially",
        HelpName = "value"
    };

    public static Option<bool> UnwrapBatch { get; } = new("--unwrapBatch", "-u")
    {
        Description = "If true then each batched request will be unwraped to single requests"
    };
}
