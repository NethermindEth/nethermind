// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Evm.Tracing;

namespace Nethermind.Test.Runner;

public class BlockchainTestsRunner(
    ITestSourceLoader testsSource,
    string? filter,
    ulong chainId,
    bool trace = false,
    bool traceMemory = false,
    bool traceNoStack = false)
    : BlockchainTestBase, IBlockchainTestRunner
{
    private readonly ConsoleColor _defaultColour = Console.ForegroundColor;
    private readonly ITestSourceLoader _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));

    public async Task<IEnumerable<EthereumTestResult>> RunTestsAsync()
    {
        List<EthereumTestResult> testResults = new();
        IEnumerable<BlockchainTest> tests = _testsSource.LoadTests<BlockchainTest>();

        // Create a streaming tracer once for all tests if tracing is enabled
        using BlockchainTestStreamingTracer? tracer = trace
            ? new BlockchainTestStreamingTracer(new() { EnableMemory = traceMemory, DisableStack = traceNoStack })
            : null;

        foreach (BlockchainTest test in tests)
        {
            if (filter is not null && test.Name is not null && !Regex.Match(test.Name, $"^({filter})").Success)
                continue;
            Setup();

            Console.Write($"{test,-120} ");
            if (test.LoadFailure is not null)
            {
                WriteRed(test.LoadFailure);
                testResults.Add(new EthereumTestResult(test.Name, test.LoadFailure));
            }
            else
            {
                test.ChainId = chainId;

                EthereumTestResult result = await RunTest(test, tracer: tracer);
                testResults.Add(result);
                if (result.Pass)
                    WriteGreen("PASS");
                else
                    WriteRed("FAIL");
            }
        }

        return testResults;
    }

    private void WriteRed(string text)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(text);
        Console.ForegroundColor = _defaultColour;
    }

    private void WriteGreen(string text)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(text);
        Console.ForegroundColor = _defaultColour;
    }
}
