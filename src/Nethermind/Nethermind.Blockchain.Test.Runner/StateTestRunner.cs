/*
 * Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Json;

namespace Nethermind.Blockchain.Test.Runner
{
    public class StateTestsRunner : BlockchainTestBase, ITestInRunner
    {
        private IBlockchainTestsSource _testsSource;
        private IJsonSerializer _serializer = new EthereumJsonSerializer();

        public StateTestsRunner(IBlockchainTestsSource testsSource) : base(testsSource)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
            Setup(null);
        }

        private void WriteOut(string text)
        {
            Console.Out.WriteLine(text);
        }

        private void WriteErr(string text)
        {
            Console.Error.WriteLine(text);
        }

        public async Task<IEnumerable<EthereumTestResult>> RunTests()
        {
            List<EthereumTestResult> testResults = new List<EthereumTestResult>();
            IEnumerable<BlockchainTest> tests = _testsSource.LoadTests();
            foreach (BlockchainTest test in tests)
            {
                await RunTest(test);
                
                EthereumTestResult result = await RunTest(test);
                testResults.Add(result);
            }

            WriteOut(_serializer.Serialize(testResults.ToArray()));
            
            return testResults;
        }
    }
}