// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Nethermind.Network.Optimum.Fuzzer;

public sealed record FuzzerOptions
{
    public required int MessageSize { get; init; }
    public required int MessageCount { get; init; }
    public required int PublisherCount { get; init; }
    public required int SubscriberCount { get; init; }
    public required TimeSpan PublisherDelay { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required TimeSpan Retries { get; init; }

    public static FuzzerOptions FromParseResult(ParseResult p)
    {
        return new FuzzerOptions
        {
            MessageSize = p.GetValue(FuzzerCliOptions.MessageSize),
            MessageCount = p.GetValue(FuzzerCliOptions.MessageCount),
            PublisherCount = p.GetValue(FuzzerCliOptions.PublisherCount),
            SubscriberCount = p.GetValue(FuzzerCliOptions.SubscriberCount),
            PublisherDelay = p.GetValue(FuzzerCliOptions.PublisherDelay),
            Timeout = p.GetValue(FuzzerCliOptions.Timeout),
            Retries = p.GetValue(FuzzerCliOptions.Retries)
        };
    }
}

public static class FuzzerCliOptions
{
    public static readonly Option<int> MessageSize = new("--message-size")
    {
        Description = ""
    };

    public static readonly Option<int> MessageCount = new("--message-count")
    {
        Description = "",
    };

    public static readonly Option<int> PublisherCount = new("--publisher-count")
    {
        Description = "",
    };

    public static readonly Option<int> SubscriberCount = new("--subscriber-count")
    {
        Description = "",
    };

    public static readonly Option<TimeSpan> PublisherDelay = new("--publisher")
    {
        Description = "",
    };

    public static readonly Option<TimeSpan> Timeout = new("--timeout")
    {
        Description = "",
    };

    public static readonly Option<TimeSpan> Retries = new("--retries")
    {
        Description = "",
    };
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Optimum Node gRPC fuzzer")
        {
            FuzzerCliOptions.MessageSize,
            FuzzerCliOptions.MessageCount,
            FuzzerCliOptions.PublisherCount,
            FuzzerCliOptions.SubscriberCount,
            FuzzerCliOptions.PublisherDelay,
            FuzzerCliOptions.Timeout,
            FuzzerCliOptions.Retries
        };

        rootCommand.SetAction(async (parseResult, token) =>
        {
            var fuzzerOptions = FuzzerOptions.FromParseResult(parseResult);
            var app = new Application(fuzzerOptions);
            await app.RunAsync(token);
        });

        CommandLineConfiguration cli = new(rootCommand);
        return await cli.InvokeAsync(args);
    }
}
