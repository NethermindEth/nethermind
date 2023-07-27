// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CommandLine;
using Nethermind.Tools.Kute.MetricsConsumer;

namespace Nethermind.Tools.Kute;

public class Config
{
    [Option(
        shortName: 'i',
        longName: "input",
        Required = true,
        HelpText = "Path to a Folder or a File containing JSON RPC messages"
    )]
    public string MessagesFilePath { get; }

    [Option(
        shortName: 'a',
        longName: "address",
        Required = false,
        Default = "http://localhost:8551",
        HelpText = "Address where to send JSON RPC requests"
    )]
    public string HostAddress { get; }

    [Option(
        shortName: 's',
        longName: "secret",
        Required = true,
        HelpText = "Path to File with hex encoded secret for JWT authentication"
    )]
    public string JwtSecretFilePath { get; }

    [Option(
        shortName: 't',
        longName: "ttl",
        Required = false,
        Default = 60,
        HelpText = "Authentication time to live (ttl) in seconds"
    )]
    public int AuthTtl { get; }

    [Option(
        shortName: 'd',
        longName: "dry",
        Required = false,
        Default = false,
        HelpText = "Only log into console"
    )]
    public bool DryRun { get; }

    [Option(
        shortName: 'p',
        longName: "progress",
        Required = false,
        Default = false,
        HelpText = "Show progress"
    )]
    public bool ShowProgress { get; }

    [Option(
        shortName: 'o',
        longName: "output",
        Required = false,
        Default = MetricsOutputFormatter.Report,
        HelpText = "Strategy to report metrics"
    )]
    public MetricsOutputFormatter MetricsOutputFormatter { get; }

    [Option(
        shortName: 'f',
        longName: "filters",
        Separator = ',',
        Required = false,
        Default = new string[] { },
        HelpText = "A comma separated List of regexes of methods to be executed with optional limits"
    )]
    public IEnumerable<string> MethodFilters { get; }

    [Option(
        shortName: 'r',
        longName: "responses",
        Required = false,
        Default = null,
        HelpText = "Path to File to store JSON-RPC responses"
    )]
    public string? ResponsesTraceFile { get; }

    public Config(
        string messagesFilePath,
        string hostAddress,
        string jwtSecretFilePath,
        int authTtl,
        bool dryRun,
        bool showProgress,
        MetricsOutputFormatter metricsOutputFormatter,
        IEnumerable<string> methodFilters,
        string? responsesTraceFile
    )
    {
        MessagesFilePath = messagesFilePath;
        HostAddress = hostAddress;
        JwtSecretFilePath = jwtSecretFilePath;
        AuthTtl = authTtl;
        DryRun = dryRun;
        ShowProgress = showProgress;
        MetricsOutputFormatter = metricsOutputFormatter;
        MethodFilters = methodFilters;
        ResponsesTraceFile = responsesTraceFile;
    }
}
