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

public class BlockchainTestsRunner : BlockchainTestBase, IBlockchainTestRunner
{
    protected override ILogManager? ComponentLogManagerOverride => suppressOutput ? new TestLogManager(LogLevel.Error) : null;

    private readonly ConsoleColor _defaultColor = Console.ForegroundColor;
    private readonly ITestSourceLoader? _testsSource;
    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();
    private readonly Regex? _filterRegex;
    private readonly ulong chainId;
    private readonly bool trace;
    private readonly bool traceMemory;
    private readonly bool traceNoStack;
    private readonly bool jsonOutput;
    private readonly bool suppressOutput;
    private readonly Func<bool, string?>? progressUpdateFactory;

    public BlockchainTestsRunner(
        ITestSourceLoader testsSource,
        string? filter,
        ulong chainId,
        bool trace = false,
        bool traceMemory = false,
        bool traceNoStack = false,
        bool jsonOutput = false,
        bool suppressOutput = false,
        Func<bool, string?>? progressUpdateFactory = null)
    {
        _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
        _filterRegex = filter is not null ? new Regex($"^({filter})", RegexOptions.Compiled) : null;
        this.chainId = chainId;
        this.trace = trace;
        this.traceMemory = traceMemory;
        this.traceNoStack = traceNoStack;
        this.jsonOutput = jsonOutput;
        this.suppressOutput = suppressOutput;
        this.progressUpdateFactory = progressUpdateFactory;
    }

    /// <summary>
    /// Lightweight constructor for RunSingleTestAsync — skips ITestSourceLoader allocation.
    /// </summary>
    public BlockchainTestsRunner(
        string? filter,
        ulong chainId,
        bool trace = false,
        bool traceMemory = false,
        bool traceNoStack = false,
        bool jsonOutput = false,
        bool suppressOutput = false,
        Func<bool, string?>? progressUpdateFactory = null)
    {
        _testsSource = null;
        _filterRegex = filter is not null ? new Regex($"^({filter})", RegexOptions.Compiled) : null;
        this.chainId = chainId;
        this.trace = trace;
        this.traceMemory = traceMemory;
        this.traceNoStack = traceNoStack;
        this.jsonOutput = jsonOutput;
        this.suppressOutput = suppressOutput;
        this.progressUpdateFactory = progressUpdateFactory;
    }

    public async Task<IEnumerable<EthereumTestResult>> RunTestsAsync()
    {
        if (_testsSource is null)
            throw new InvalidOperationException("RunTestsAsync requires a test source; use the constructor that accepts ITestSourceLoader.");

        List<EthereumTestResult> testResults = [];
        IEnumerable<EthereumTest> tests = _testsSource.LoadTests<EthereumTest>();
        foreach (EthereumTest loadedTest in tests)
        {
            if (loadedTest is FailedToLoadTest)
            {
                if (!jsonOutput && !suppressOutput) WriteRed(loadedTest.LoadFailure);
                testResults.Add(new EthereumTestResult(loadedTest.Name, loadedTest.LoadFailure));
                if (suppressOutput)
                    WriteStatus("EXCEPTION", progressUpdateFactory?.Invoke(true), loadedTest.LoadFailure, false);
                continue;
            }

            if (loadedTest is not BlockchainTest test)
                continue;

            // Create a streaming tracer once for all tests if tracing is enabled
            using BlockchainTestStreamingTracer? tracer = trace
                ? new BlockchainTestStreamingTracer(new() { EnableMemory = traceMemory, DisableStack = traceNoStack })
                : null;

            if (_filterRegex is not null && test.Name is not null && !_filterRegex.IsMatch(test.Name))
                continue;

            if (!jsonOutput && !suppressOutput) Console.Write($"{test,-120} ");
            if (test.LoadFailure is not null)
            {
                if (!jsonOutput && !suppressOutput) WriteRed(test.LoadFailure);
                testResults.Add(new EthereumTestResult(test.Name, test.LoadFailure));
                if (suppressOutput)
                    WriteStatus("EXCEPTION", progressUpdateFactory?.Invoke(true), test.LoadFailure, false);
            }
            else
            {
                test.ChainId = chainId;

                try
                {
                    EthereumTestResult result = await RunTest(test, tracer: tracer);
                    testResults.Add(result);
                    if (suppressOutput)
                    {
                        string? progress = progressUpdateFactory?.Invoke(!result.Pass);
                        if (result.Pass)
                        {
                            WriteProgress(progress);
                        }
                        else
                        {
                            WriteStatus("FAIL", progress, test.Name, false);
                        }
                    }
                    else if (!jsonOutput)
                    {
                        if (result.Pass)
                            WriteGreen("PASS");
                        else
                            WriteRed("FAIL");
                    }
                }
                catch (Exception ex)
                {
                    testResults.Add(new EthereumTestResult(test.Name, test.ForkName, ex.Message));
                    if (suppressOutput)
                        WriteStatus("EXCEPTION", progressUpdateFactory?.Invoke(true), $"{test.Name} — {ex.Message}", false);
                    else if (!jsonOutput)
                        WriteRed($"EXCEPTION: {ex.Message}");
                }
            }
        }

        if (jsonOutput && !suppressOutput)
        {
            Console.Out.Write(_serializer.Serialize(testResults, true));
        }

        return testResults;
    }

    public async Task<EthereumTestResult> RunSingleTestAsync(BlockchainTest test)
    {
        test.ChainId = chainId;
        return await RunTest(test);
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
