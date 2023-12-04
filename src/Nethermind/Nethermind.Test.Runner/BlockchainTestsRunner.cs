// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;

namespace Nethermind.Test.Runner;

public class BlockchainTestsRunner : BlockchainTestBase, IBlockchainTestRunner
{
    private ITestSourceLoader _testsSource;
    private readonly string? _filter;
    private ConsoleColor _defaultColour;

    public BlockchainTestsRunner(ITestSourceLoader testsSource, string? filter)
    {
        _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
        _filter = filter;
        _defaultColour = Console.ForegroundColor;
    }

    public async Task<IEnumerable<EthereumTestResult>> RunTestsAsync()
    {
        List<EthereumTestResult> testResults = new();
        string directoryName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FailingTests");
        IEnumerable<BlockchainTest> tests = (IEnumerable<BlockchainTest>)_testsSource.LoadTests();
        foreach (BlockchainTest test in tests)
        {
            if (_filter is not null && !test.Name.StartsWith(_filter))
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
                {
                    WriteRed("FAIL");
                    if (!Directory.Exists(directoryName))
                        Directory.CreateDirectory(directoryName);
                    Setup();
                    await RunTest(test);
                }
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
