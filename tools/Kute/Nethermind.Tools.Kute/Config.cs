// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.Tools.Kute.Metrics;

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
        HelpName = "url",
        Required = true,
    };

    public static Option<string> JwtSecretFilePath { get; } = new("--secret", "-s")
    {
        Description = "Path to file with hex-encoded secret for JWT authentication",
        HelpName = "path",
        Required = true
    };

    public static Option<int> AuthTtl { get; } = new("--ttl", "-t")
    {
        DefaultValueFactory = r => 60,
        Description = "Authentication time to live (TTL), in seconds",
        HelpName = "number"
    };

    public static Option<bool> ShowProgress { get; } = new("--progress", "-p")
    {
        Description = "Show progress",
        DefaultValueFactory = r => false,
    };

    public static Option<MetricsReportFormat> MetricsReportFormatter { get; } = new("--output", "-o")
    {
        DefaultValueFactory = r => MetricsReportFormat.Pretty,
        Description = "Strategy to report metrics",
        HelpName = "path",
    };

    public static Option<IEnumerable<string>> MethodFilters { get; } = new("--filters", "-f")
    {
        Description = "A comma separated List of regexes of methods to be executed with optional limits",
        HelpName = "regexes",
        CustomParser = r => r.Tokens.Count == 1 ? r.Tokens[0].Value.Split(',') : null,
        DefaultValueFactory = r => [],
    };

    public static Option<string> ResponsesTraceFile { get; } = new("--responses", "-r")
    {
        Description = "Path to file to store JSON-RPC responses",
        HelpName = "path"
    };

    public static Option<int> ConcurrentRequests { get; } = new("--concurrency", "-c")
    {
        Description = "Process at most <number> request concurrently",
        HelpName = "number",
        DefaultValueFactory = r => 1
    };

    public static Option<string?> PrometheusPushGateway { get; } = new("--gateway", "-g")
    {
        Description = "Prometheus Push Gateway endpoint to report metrics",
        HelpName = "url",
    };

    public static Option<bool> UnwrapBatch { get; } = new("--unwrapBatch", "-u")
    {
        Description = "Batch requests will be unwraped to single requests"
    };
}
