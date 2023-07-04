// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
using Nethermind.Logging;
using Nethermind.Logging.NLog;

namespace Nethermind.Blockchain.Test.Runner
{
    public class StateTestsBugHunter : GeneralStateTestBase, IStateTestRunner
    {
        private ITestSourceLoader _testsSource;
        private ConsoleColor _defaultColour;

        public StateTestsBugHunter(ITestSourceLoader testsSource)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
            _defaultColour = Console.ForegroundColor;
        }

        public IEnumerable<EthereumTestResult> RunTests()
        {
            List<EthereumTestResult> testResults = new List<EthereumTestResult>();
            string directoryName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FailingTests");
            IEnumerable<GeneralStateTest> tests = (IEnumerable<GeneralStateTest>)_testsSource.LoadTests();
            foreach (GeneralStateTest test in tests)
            {
                Setup(LimboLogs.Instance);

                Console.Write($"{test,-120} ");
                if (test.LoadFailure != null)
                {
                    WriteRed(test.LoadFailure);
                    testResults.Add(new EthereumTestResult(test.Name, test.ForkName, test.LoadFailure));
                }
                else
                {
                    EthereumTestResult result = RunTest(test);
                    testResults.Add(result);

                    if (result.Pass)
                    {
                        WriteGreen("PASS");
                    }
                    else
                    {
                        WriteRed("FAIL");
                        NLogManager manager = new NLogManager(string.Concat(test.Category, "_", test.Name, ".txt"), directoryName);
                        if (!Directory.Exists(directoryName))
                        {
                            Directory.CreateDirectory(directoryName);
                        }

                        Setup(manager);
                        RunTest(test);
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
}
