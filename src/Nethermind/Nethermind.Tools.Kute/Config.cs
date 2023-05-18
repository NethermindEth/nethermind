// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CommandLine;
using Nethermind.Tools.Kute.MetricsConsumer;

namespace Nethermind.Tools.Kute;

public class Config
{
    [Option(
        shortName: 'm',
        longName: "messages",
        Required = true,
        HelpText = "Path to a Folder or a File containing JSON RPC messages"
    )]
    public string MessagesSourcePath { get; }

    [Option(
        shortName: 'a',
        longName: "address",
        Required = false,
        Default = "http://localhost:8551",
        HelpText = "Address where to send JSON RPC calls"
    )]
    public string HostAddress { get; }

    [Option(
        shortName: 's',
        longName: "secret",
        Required = true,
        HelpText = "Path to file with hex encoded secret for jwt authentication"
    )]
    public string JwtSecretFile { get; }

    [Option(
        shortName: 'd',
        longName: "dry",
        Required = false,
        Default = false,
        HelpText = "Only log into console"
    )]
    public bool DryRun { get; }

    [Option(
        shortName: 'o',
        longName: "output",
        Required = false,
        Default = MetricConsumerStrategy.Report,
        HelpText = "Strategy to report metrics"
    )]
    public MetricConsumerStrategy MetricConsumerStrategy { get; }

    [Option(
        shortName: 'f',
        longName: "filters",
        Separator = ',',
        Required = false,
        Default = new string[] { },
        HelpText = "A comma separated List of regexes of methods to be executed"
    )]
    public IEnumerable<string> MethodFilters { get; }

    public Config(
        string messagesSourcePath,
        string hostAddress,
        string jwtSecretFile,
        bool dryRun,
        MetricConsumerStrategy metricConsumerStrategy,
        IEnumerable<string> methodFilters
    )
    {
        MessagesSourcePath = messagesSourcePath;
        HostAddress = hostAddress;
        JwtSecretFile = jwtSecretFile;
        DryRun = dryRun;
        MetricConsumerStrategy = metricConsumerStrategy;
        MethodFilters = methodFilters;
    }
}
