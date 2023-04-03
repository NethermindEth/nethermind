// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
using Nethermind.Logging;
using Nethermind.Logging.NLog;

namespace Nethermind.Blockchain.Test.Runner
{
    public class BlockchainTestsBugHunter : BlockchainTestBase, IBlockchainTestRunner
    {
        private ITestSourceLoader _testsSource;
        private ConsoleColor _defaultColour;

        public BlockchainTestsBugHunter(ITestSourceLoader testsSource)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
            _defaultColour = Console.ForegroundColor;
        }

        public async Task<IEnumerable<EthereumTestResult>> RunTestsAsync()
        {
            List<EthereumTestResult> testResults = new List<EthereumTestResult>();
            string directoryName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FailingTests");
            IEnumerable<BlockchainTest> tests = (IEnumerable<BlockchainTest>)_testsSource.LoadTests();
            foreach (BlockchainTest test in tests)
            {
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
}
