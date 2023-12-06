// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;

namespace Nethermind.Test.Runner;

public class BlockchainTestsRunner : BlockchainTestBase, IBlockchainTestRunner
{
    private readonly ConsoleColor _defaultColour;
    private readonly ITestSourceLoader _testsSource;
    private readonly string? _filter;

    public BlockchainTestsRunner(ITestSourceLoader testsSource, string? filter)
    {
        _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
        _defaultColour = Console.ForegroundColor;
        _filter = filter;
    }

    public async Task<IEnumerable<EthereumTestResult>> RunTestsAsync()
    {
        List<EthereumTestResult> testResults = new();
        IEnumerable<BlockchainTest> tests = (IEnumerable<BlockchainTest>)_testsSource.LoadTests();
        foreach (BlockchainTest test in tests)
        {
            if (_filter is not null && !Regex.Match(test.Name, $"^({_filter})").Success)
                continue;
            Setup();

            Console.Write($"{test,-120} ");
            if (test.LoadFailure != null)
            {
                WriteRed(test.LoadFailure);
                testResults.Add(new EthereumTestResult(test.Name, test.LoadFailure));
            }
            else
            {
                EthereumTestResult result = await RunTest(test);
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
