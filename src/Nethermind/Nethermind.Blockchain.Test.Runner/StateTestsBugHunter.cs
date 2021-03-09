/*
 * Copyright (c) 2021 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
