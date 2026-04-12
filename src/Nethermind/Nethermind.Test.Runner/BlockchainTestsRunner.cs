// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Serialization.Json;

namespace Nethermind.Test.Runner;

public class BlockchainTestsRunner : BlockchainTestBase, IBlockchainTestRunner
{
    private readonly ConsoleColor _defaultColor = Console.ForegroundColor;
    private readonly ITestSourceLoader? _testsSource;
    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();
    private readonly string? filter;
    private readonly ulong chainId;
    private readonly bool trace;
    private readonly bool traceMemory;
    private readonly bool traceNoStack;
    private readonly bool jsonOutput;
    private readonly bool suppressOutput;

    public BlockchainTestsRunner(
        ITestSourceLoader testsSource,
        string? filter,
        ulong chainId,
        bool trace = false,
        bool traceMemory = false,
        bool traceNoStack = false,
        bool jsonOutput = false,
        bool suppressOutput = false)
    {
        _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
        this.filter = filter;
        this.chainId = chainId;
        this.trace = trace;
        this.traceMemory = traceMemory;
        this.traceNoStack = traceNoStack;
        this.jsonOutput = jsonOutput;
        this.suppressOutput = suppressOutput;
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
        bool suppressOutput = false)
    {
        _testsSource = null;
        this.filter = filter;
        this.chainId = chainId;
        this.trace = trace;
        this.traceMemory = traceMemory;
        this.traceNoStack = traceNoStack;
        this.jsonOutput = jsonOutput;
        this.suppressOutput = suppressOutput;
    }

    public async Task<IEnumerable<EthereumTestResult>> RunTestsAsync()
    {
        List<EthereumTestResult> testResults = [];
        IEnumerable<EthereumTest> tests = _testsSource!.LoadTests<EthereumTest>();
        foreach (EthereumTest loadedTest in tests)
        {
            if (loadedTest as FailedToLoadTest is not null)
            {
                if (!jsonOutput && !suppressOutput) WriteRed(loadedTest.LoadFailure);
                testResults.Add(new EthereumTestResult(loadedTest.Name, loadedTest.LoadFailure));
                continue;
            }

            // Create a streaming tracer once for all tests if tracing is enabled
            using BlockchainTestStreamingTracer? tracer = trace
                ? new BlockchainTestStreamingTracer(new() { EnableMemory = traceMemory, DisableStack = traceNoStack })
                : null;

            BlockchainTest test = loadedTest as BlockchainTest;

            if (filter is not null && test.Name is not null && !Regex.Match(test.Name, $"^({filter})").Success)
                continue;

            if (!jsonOutput && !suppressOutput) Console.Write($"{test,-120} ");
            if (test.LoadFailure is not null)
            {
                if (!jsonOutput && !suppressOutput) WriteRed(test.LoadFailure);
                testResults.Add(new EthereumTestResult(test.Name, test.LoadFailure));
            }
            else
            {
                test.ChainId = chainId;

                EthereumTestResult result = await RunTest(test, tracer: tracer);
                testResults.Add(result);
                if (!jsonOutput && !suppressOutput)
                {
                    if (result.Pass)
                        WriteGreen("PASS");
                    else
                        WriteRed("FAIL");
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
}
