// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Nethermind.Network.Optimum.Fuzzer;

public sealed record FuzzerOptions
{
    public required Uri GrpcEndpoint { get; init; }
    public required int MessageSize { get; init; }
    public required int MessageCount { get; init; }
    public required int PublisherCount { get; init; }
    public required int SubscriberCount { get; init; }
    public required TimeSpan PublisherDelay { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required TimeSpan Runs { get; init; }

    public static FuzzerOptions FromParseResult(ParseResult p)
    {
        return new FuzzerOptions
        {
            GrpcEndpoint = new Uri(p.GetValue(FuzzerCliOptions.GrpcEndpoint)!),
            MessageSize = p.GetValue(FuzzerCliOptions.MessageSize),
            MessageCount = p.GetValue(FuzzerCliOptions.MessageCount),
            PublisherCount = p.GetValue(FuzzerCliOptions.PublisherCount),
            SubscriberCount = p.GetValue(FuzzerCliOptions.SubscriberCount),
            PublisherDelay = TimeSpan.FromMilliseconds(p.GetValue(FuzzerCliOptions.PublisherDelay)),
            Timeout = TimeSpan.FromMilliseconds(p.GetValue(FuzzerCliOptions.Timeout)),
            Runs = TimeSpan.FromMilliseconds(p.GetValue(FuzzerCliOptions.Runs))
        };
    }
}

public static class FuzzerCliOptions
{
    public static readonly Option<string> GrpcEndpoint = new("--address")
    {
        Description = "The Optimum Node gRPC endpoint",
        Required = true,
    };

    public static readonly Option<int> MessageSize = new Option<int>("--message-size")
    {
        Description = "The size (in bytes) of each message to send",
        DefaultValueFactory = (_) => 1024 // Default to 1kb
    }.Validated(Validators.Positive);

    public static readonly Option<int> MessageCount = new Option<int>("--message-count")
    {
        Description = "The number of messages to send per publisher",
        DefaultValueFactory = (_) => 100_000 // Default to 100_000 messages
    }.Validated(Validators.Positive);

    public static readonly Option<int> PublisherCount = new Option<int>("--publisher-count")
    {
        Description = "The number of concurrent publishers",
        DefaultValueFactory = (_) => 1 // Default to 1 publisher
    }.Validated(Validators.Positive);

    public static readonly Option<int> SubscriberCount = new Option<int>("--subscriber-count")
    {
        Description = "The number of concurrent subscribers",
        DefaultValueFactory = (_) => 1 // Default to 1 subscriber
    }.Validated(Validators.Positive);

    public static readonly Option<int> PublisherDelay = new Option<int>("--publisher-timeout")
    {
        Description = "Delay between publishing messages (milliseconds)",
        DefaultValueFactory = (_) => 1 // 1000 microseconds
    }.Validated(Validators.Positive);

    public static readonly Option<int> Timeout = new Option<int>("--timeout")
    {
        Description = "Timeout for each run (milliseconds)",
        DefaultValueFactory = (_) => 60_000 // 1 minute
    }.Validated(Validators.Positive);

    public static readonly Option<int> Runs = new Option<int>("--runs")
    {
        Description = "Number of times to repeat each run",
        DefaultValueFactory = (_) => 1
    }.Validated(Validators.Positive);

    public static readonly Option<LogLevel> Logging = new Option<LogLevel>("--log")
    {
        Description = "Log level",
        DefaultValueFactory = (_) => LogLevel.Information
    };
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Optimum Node gRPC fuzzer")
        {
            FuzzerCliOptions.GrpcEndpoint,
            FuzzerCliOptions.MessageSize,
            FuzzerCliOptions.MessageCount,
            FuzzerCliOptions.PublisherCount,
            FuzzerCliOptions.SubscriberCount,
            FuzzerCliOptions.PublisherDelay,
            FuzzerCliOptions.Timeout,
            FuzzerCliOptions.Runs,
            FuzzerCliOptions.Logging
        };

        rootCommand.SetAction(async (parseResult, token) =>
        {
            var fuzzerOptions = FuzzerOptions.FromParseResult(parseResult);
            using ILoggerFactory factory = LoggerFactory.Create(builder =>
                builder
                    .AddConsole()
                    .SetMinimumLevel(parseResult.GetValue(FuzzerCliOptions.Logging))
            );
            ILogger logger = factory.CreateLogger<Application>();
            var app = new Application(fuzzerOptions, logger);

            await app.RunAsync(token);
        });

        CommandLineConfiguration cli = new(rootCommand);
        return await cli.InvokeAsync(args);
    }
}

public static class Extensions
{
    public static Option<T> Validated<T>(this Option<T> @this, Func<T, IEnumerable<string>> validator)
    {
        var name = @this.Name;
        @this.Validators.Add(result =>
        {
            foreach (var error in validator(result.GetValue(@this)!))
            {
                result.AddError($"Option '{name}' {error}.");
            }
        });
        return @this;
    }
}

public static class Validators
{
    public static IEnumerable<string> Positive<T>(T value) where T : INumber<T>
    {
        if (value <= T.Zero)
        {
            yield return "must be positive";
        }
    }
}
