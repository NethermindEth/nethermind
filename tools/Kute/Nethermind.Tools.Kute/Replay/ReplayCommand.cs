// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>The <c>replay</c> subcommand: replays a captured trace across a concurrency sweep.</summary>
/// <remarks>
/// Kept separate from the root command because the two have different shapes. The root command drives
/// the authenticated Engine API, while a state-reading trace is replayed against the JSON-RPC port
/// with no secret, and needs sweep, warm-up and block-parameter controls the root command has no use for.
/// </remarks>
public static class ReplayCommand
{
    /// <summary>Failure rate above which the run reports failure, matching the RPC benchmark gate.</summary>
    private const double DefaultMaxFailurePercent = 1.0d;

    private static readonly Option<string> Input = new("--input", "-i")
    {
        Description = "Path to a trace of newline-delimited JSON-RPC requests (.jsonl, .jsonl.gz or .jsonl.zst)",
        HelpName = "path",
        Required = true,
    };

    private static readonly Option<string> Address = new("--address", "-a")
    {
        Description = "Endpoint receiving the requests",
        HelpName = "url",
        DefaultValueFactory = _ => "http://localhost:8545",
    };

    private static readonly Option<string> Concurrency = new("--concurrency", "-c")
    {
        Description = "Concurrency levels: a doubling range (1-64), an explicit list (1,4,12) or a single level",
        HelpName = "spec",
        DefaultValueFactory = _ => "1",
    };

    private static readonly Option<string> BlockTag = new("--block", "-b")
    {
        Description = "Block parameter forced on every request; 'keep' replays the captured one",
        HelpName = "tag",
        DefaultValueFactory = _ => "latest",
    };

    private static readonly Option<int> Requests = new("--requests", "-n")
    {
        Description = "Requests measured per level; 0 replays the whole trace",
        HelpName = "count",
        DefaultValueFactory = _ => 2000,
    };

    private static readonly Option<int> Warmup = new("--warmup", "-w")
    {
        Description = "Requests sent and discarded before each measured window",
        HelpName = "count",
        DefaultValueFactory = _ => 200,
    };

    private static readonly Option<int> Skip = new("--skip")
    {
        Description = "Records skipped at the start of the trace",
        HelpName = "count",
        DefaultValueFactory = _ => 0,
    };

    private static readonly Option<int> Duration = new("--duration", "-d")
    {
        Description = "Stop a level once its measured window reaches this many seconds; 0 for no cap",
        HelpName = "seconds",
        DefaultValueFactory = _ => 0,
    };

    private static readonly Option<int> Timeout = new("--timeout")
    {
        Description = "Per-request HTTP timeout, in seconds",
        HelpName = "seconds",
        DefaultValueFactory = _ => 120,
    };

    private static readonly Option<string?> Secret = new("--secret", "-s")
    {
        Description = "Path to a hex-encoded JWT secret, for endpoints that require authentication",
        HelpName = "path",
    };

    private static readonly Option<ReplayReportFormat> Output = new("--output", "-o")
    {
        Description = "Report format",
        DefaultValueFactory = _ => ReplayReportFormat.Pretty,
    };

    private static readonly Option<string?> OutputFile = new("--output-file")
    {
        Description = "Write the report to this file instead of standard output",
        HelpName = "path",
    };

    private static readonly Option<double> MaxFailurePercent = new("--max-failure-rate")
    {
        Description = "Report failure if any level exceeds this percentage of failed requests",
        HelpName = "percent",
        DefaultValueFactory = _ => DefaultMaxFailurePercent,
    };

    private static readonly Option<bool> KeepFees = new("--keep-fees")
    {
        Description = "Replay captured gasPrice/maxFeePerGas fields instead of stripping them",
    };

    private static readonly Option<bool> DryRun = new("--dry-run")
    {
        Description = "Stream and rewrite the trace without sending anything, verifying every block parameter",
    };

    private static readonly Option<bool> Progress = new("--progress", "-p")
    {
        Description = "Write per-level progress to standard error",
    };

    static ReplayCommand()
    {
        RequireMinimum(Requests, 0);
        RequireMinimum(Warmup, 0);
        RequireMinimum(Skip, 0);
        RequireMinimum(Duration, 0);
        RequireMinimum(Timeout, 1);
        MaxFailurePercent.Validators.Add(static result =>
        {
            double value = result.GetValueOrDefault<double>();
            // The negated form also rejects NaN, which would otherwise disable the failure gate.
            if (!(value >= 0d && value <= 100d))
            {
                result.AddError("--max-failure-rate must be a percentage between 0 and 100.");
            }
        });
    }

    /// <summary>Rejects values below <paramref name="minimum"/> at parse time, before they reach a loop bound or HttpClient.</summary>
    private static void RequireMinimum(Option<int> option, int minimum) =>
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < minimum)
            {
                result.AddError($"{option.Name} must be at least {minimum}.");
            }
        });

    /// <summary>Builds the <c>replay</c> subcommand.</summary>
    public static Command Create()
    {
        Command command = new("replay", "Replay a captured JSON-RPC trace at a range of concurrency levels.")
        {
            Input,
            Address,
            Concurrency,
            BlockTag,
            Requests,
            Warmup,
            Skip,
            Duration,
            Timeout,
            Secret,
            Output,
            OutputFile,
            MaxFailurePercent,
            KeepFees,
            DryRun,
            Progress,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            ReplayOptions options;
            try
            {
                options = BuildOptions(parseResult);
            }
            catch (Exception e) when (e is FormatException or UriFormatException or ArgumentException)
            {
                await Console.Error.WriteLineAsync(e.Message);
                return 2;
            }

            ReplaySweep sweep = new(options, Console.Error);
            IReadOnlyList<LevelResult> results;
            try
            {
                results = await sweep.RunAsync(cancellationToken);
            }
            catch (InvalidDataException e)
            {
                await Console.Error.WriteLineAsync(e.Message);
                return 2;
            }

            string report = parseResult.GetValue(Output) switch
            {
                ReplayReportFormat.Json => ReplayReport.Json(results),
                ReplayReportFormat.Csv => ReplayReport.Csv(results),
                _ => ReplayReport.Pretty(results),
            };

            string? outputFile = parseResult.GetValue(OutputFile);
            if (outputFile is null)
            {
                await Console.Out.WriteAsync(report);
            }
            else
            {
                await File.WriteAllTextAsync(outputFile, report, cancellationToken);
            }

            foreach (LevelResult result in results)
            {
                // A level that sent nothing has a zero failure rate; exiting zero would let CI read
                // an empty trace as a passing benchmark.
                if (result.Total == 0)
                {
                    await Console.Error.WriteLineAsync("No requests were replayed: the trace has no records past --skip.");
                    return 2;
                }
            }

            return ExceedsFailureBudget(results, parseResult.GetValue(MaxFailurePercent)) ? 1 : 0;
        });

        return command;
    }

    private static ReplayOptions BuildOptions(ParseResult parseResult)
    {
        string blockTag = parseResult.GetValue(BlockTag)!;
        int duration = parseResult.GetValue(Duration);

        return new ReplayOptions
        {
            InputPath = parseResult.GetValue(Input)!,
            Address = new Uri(parseResult.GetValue(Address)!),
            Concurrencies = ConcurrencySpec.Parse(parseResult.GetValue(Concurrency)!),
            BlockTag = string.Equals(blockTag, "keep", StringComparison.OrdinalIgnoreCase) ? null : blockTag,
            StripFeeFields = !parseResult.GetValue(KeepFees),
            MeasuredRequests = parseResult.GetValue(Requests),
            WarmupRequests = parseResult.GetValue(Warmup),
            Skip = parseResult.GetValue(Skip),
            MaxDuration = duration > 0 ? TimeSpan.FromSeconds(duration) : null,
            Timeout = TimeSpan.FromSeconds(parseResult.GetValue(Timeout)),
            DryRun = parseResult.GetValue(DryRun),
            Progress = parseResult.GetValue(Progress),
            SecretPath = parseResult.GetValue(Secret),
        };
    }

    private static bool ExceedsFailureBudget(IReadOnlyList<LevelResult> results, double maxFailurePercent)
    {
        double budget = maxFailurePercent / 100d;
        foreach (LevelResult result in results)
        {
            if (result.FailureRate > budget)
            {
                return true;
            }
        }

        return false;
    }
}
