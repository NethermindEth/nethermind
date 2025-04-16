// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;

namespace Nethermind.Test.Runner;

public class EofTestsRunner(ITestSourceLoader testsSource, string? filter) : EofTestBase, IEofTestRunner
{
    private readonly ConsoleColor _defaultColour = Console.ForegroundColor;
    private readonly ITestSourceLoader _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));

    public IEnumerable<EthereumTestResult> RunTests()
    {
        List<EthereumTestResult> testResults = new();
        var tests = _testsSource.LoadTests<EofTest>();
        foreach (EofTest test in tests)
        {
            if (filter is not null && !Regex.Match(test.Name, $"^({filter})").Success)
                continue;
            Setup();

            Console.Write($"{test.Name,-120} ");
            if (test.LoadFailure is not null)
            {
                WriteRed(test.LoadFailure);
                testResults.Add(new EthereumTestResult(test.Name, test.LoadFailure));
            }
            else
            {
                var result = new EthereumTestResult(test.Name, "Osaka", RunTest(test));
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
