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
using Nethermind.Evm.Tracing;

namespace Nethermind.State.Test.Runner
{
    public class StateTestsRunner : BlockchainTestBase, IStateTestRunner
    {
        private IBlockchainTestsSource _testsSource;
        private readonly bool _alwaysTrace;
        private IJsonSerializer _serializer = new EthereumJsonSerializer();

        public StateTestsRunner(IBlockchainTestsSource testsSource, bool alwaysTrace)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
            _alwaysTrace = alwaysTrace;
            Setup(null);
        }

        private void WriteOut(List<EthereumTestResult> testResult)
        {
            Console.Out.Write(_serializer.Serialize(testResult, true));
        }

        private void WriteErr(StateTestTxTrace txTrace)
        {
            foreach (var entry in txTrace.Entries)
            {
                Console.Error.WriteLine(_serializer.Serialize(entry));
            }
            
            Console.Error.WriteLine(_serializer.Serialize(txTrace.Result));
            Console.Error.WriteLine(_serializer.Serialize(txTrace.State));
        }

        public IEnumerable<EthereumTestResult> RunTests()
        {
            List<EthereumTestResult> results = new List<EthereumTestResult>();
            IEnumerable<BlockchainTest> tests = _testsSource.LoadTests();
            foreach (BlockchainTest test in tests)
            {
                EthereumTestResult result = null;
                if (!_alwaysTrace)
                {
                    result = RunTest(test, NullTxTracer.Instance);
                }

                if (!(result?.Pass ?? false))
                {
                    StateTestTxTracer txTracer = new StateTestTxTracer();
                    result = RunTest(test, txTracer);

                    var txTrace = txTracer.BuildResult();
                    txTrace.Result.Time = result.TimeInMs;
                    txTrace.State.StateRoot = result.StateRoot;
                
                    WriteErr(txTrace);    
                }
                
                results.Add(result);
            }
            
            WriteOut(results);
            
            return results;
        }
    }
}