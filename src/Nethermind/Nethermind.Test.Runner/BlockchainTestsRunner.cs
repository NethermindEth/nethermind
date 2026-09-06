// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Test.Runner;

public readonly record struct BlockchainTestsRunnerOptions(
    Regex? Filter = null,
    ulong ChainId = 0,
    bool Trace = false,
    bool TraceMemory = false,
    bool ExcludeStack = false,
    bool JsonOutput = false,
    bool SuppressOutput = false,
    bool? ParallelExecution = null,
    bool? ParallelExecutionBatchRead = null,
    Func<bool, string?>? ProgressUpdateFactory = null);

public class BlockchainTestsRunner(in BlockchainTestsRunnerOptions options, ITestSourceLoader? testsSource = null) : BlockchainTestBase, IBlockchainTestRunner
{
    protected override ILogManager? ComponentLogManagerOverride => _suppressOutput ? new TestLogManager(LogLevel.Error) : null;
    protected override bool? ParallelExecutionOverride => _parallelExecution;
    protected override bool? ParallelExecutionBatchReadOverride => _parallelExecutionBatchRead;
    private readonly ConsoleColor _defaultColor = Console.ForegroundColor;
    private readonly ITestSourceLoader? _testsSource = testsSource;
    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();
    // Compiled once by the caller and shared across the per-file runner instances.
    private readonly Regex? _filterRegex = options.Filter;
    private readonly ulong _chainId = options.ChainId;
    private readonly bool _trace = options.Trace;
    private readonly bool _traceMemory = options.TraceMemory;
    private readonly bool _excludeStack = options.ExcludeStack;
    private readonly bool _jsonOutput = options.JsonOutput;
    private readonly bool _suppressOutput = options.SuppressOutput;
    private readonly bool? _parallelExecution = options.ParallelExecution;
    private readonly bool? _parallelExecutionBatchRead = options.ParallelExecutionBatchRead;
    private readonly Func<bool, string?>? _progressUpdateFactory = options.ProgressUpdateFactory;

    public BlockchainTestsRunner(ITestSourceLoader testsSource, in BlockchainTestsRunnerOptions options)
        : this(options, testsSource ?? throw new ArgumentNullException(nameof(testsSource)))
    {
    }

    public async Task<IEnumerable<EthereumTestResult>> RunTestsAsync()
    {
        if (_testsSource is null)
            throw new InvalidOperationException("RunTestsAsync requires a test source; use the constructor that accepts ITestSourceLoader.");

        List<EthereumTestResult> testResults = [];
        IEnumerable<EthereumTest> tests = _testsSource.LoadTests<EthereumTest>();
        foreach (EthereumTest loadedTest in tests)
        {
            EthereumTestResult? result = await ExecuteTestAsync(loadedTest);
            if (result is null)
                continue;

            testResults.Add(result);
            ReportResult(result);
        }

        if (_jsonOutput && !_suppressOutput)
        {
            Console.Out.Write(_serializer.Serialize(testResults, true));
        }

        return testResults;
    }

    public async Task<EthereumTestResult> RunSingleTestAsync(BlockchainTest test)
    {
        test.ChainId = _chainId;
        return await RunTest(test);
    }

    private async Task<EthereumTestResult?> ExecuteTestAsync(EthereumTest loadedTest)
    {
        if (loadedTest is FailedToLoadTest)
            return new EthereumTestResult(loadedTest.Name, loadedTest.LoadFailure);

        if (loadedTest is not BlockchainTest test)
            return null;

        if (_filterRegex is not null && test.Name is not null && !_filterRegex.IsMatch(test.Name))
            return null;

        if (test.LoadFailure is not null)
            return new EthereumTestResult(test.Name, test.LoadFailure);

        test.ChainId = _chainId;

        try
        {
            // Intentionally created per test: each test emits an independent JSONL trace,
            // so the tracer's block counter resetting between tests is by design.
            using BlockchainTestStreamingTracer? tracer = _trace
                ? new BlockchainTestStreamingTracer(new() { EnableMemory = _traceMemory, DisableStack = _excludeStack })
                : null;

            return await RunTest(test, tracer: tracer);
        }
        catch (Exception ex)
        {
            return new EthereumTestResult(test.Name, test.ForkName, ex.ToString());
        }
    }

    private void ReportResult(EthereumTestResult result)
    {
        if (_suppressOutput)
        {
            ReportSuppressedResult(result);
            return;
        }

        if (_jsonOutput)
            return;

        Console.Write($"{result.Name,-120} ");
        if (result.Pass)
            WriteGreen("PASS");
        else if (result.LoadFailure is not null)
            WriteRed(result.LoadFailure);
        else if (result.Error is not null)
            WriteRed($"EXCEPTION: {result.Error}");
        else
            WriteRed("FAIL");
    }

    private void ReportSuppressedResult(EthereumTestResult result)
    {
        string? progress = _progressUpdateFactory?.Invoke(!result.Pass);
        if (result.Pass)
        {
            WriteProgress(progress);
            return;
        }

        // Always lead with the failing test's name, then the reason, so a failure line
        // identifies *which* test failed instead of only printing the exception/error.
        string status = result.LoadFailure is not null ? "EXCEPTION" : "FAIL";
        string? reason = result.LoadFailure ?? result.Error;
        string message = reason is null ? result.Name : $"{result.Name} - {reason}";
        WriteStatus(status, progress, message, false);
    }

    private void WriteRed(string text)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(text);
        Console.ForegroundColor = _defaultColor;
    }

    private void WriteGreen(string text)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(text);
        Console.ForegroundColor = _defaultColor;
    }

    private static void WriteProgress(string? progress)
    {
        if (progress is null)
            return;

        Console.Error.WriteLine($"PROGRESS {progress}");
        Console.Error.Flush();
    }

    private static void WriteStatus(string prefix, string? progress, string message, bool pass)
    {
        string color = pass ? "\x1b[32m" : "\x1b[31m";
        string progressPart = string.IsNullOrEmpty(progress) ? "" : $"{progress} ";
        Console.Error.WriteLine($"{color}{prefix}\x1b[0m {progressPart}{message}");
        Console.Error.Flush();
    }
}
