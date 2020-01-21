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
using Ethereum.Test.Base;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Json;

namespace Nethermind.State.Test.Runner
{
    public enum WhenTrace
    {
        WhenFailing,
        Always,
        Never
    }
    
    public class StateTestsRunner : BlockchainTestBase, IStateTestRunner
    {
        private IBlockchainTestsSource _testsSource;
        private readonly WhenTrace _whenTrace;
        private readonly bool _traceMemory;
        private readonly bool _traceStack;
        private IJsonSerializer _serializer = new EthereumJsonSerializer();

        public StateTestsRunner(IBlockchainTestsSource testsSource, WhenTrace whenTrace, bool traceMemory, bool traceStack)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
            _whenTrace = whenTrace;
            _traceMemory = traceMemory;
            _traceStack = traceStack;
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

        private IntrinsicGasCalculator _calculator = new IntrinsicGasCalculator();
        
        public IEnumerable<EthereumTestResult> RunTests()
        {
            List<EthereumTestResult> results = new List<EthereumTestResult>();
            IEnumerable<BlockchainTest> tests = _testsSource.LoadTests();
            foreach (BlockchainTest test in tests)
            {
                EthereumTestResult result = null;
                if (_whenTrace != WhenTrace.Always)
                {
                    result = RunTest(test, NullTxTracer.Instance);
                }

                if (_whenTrace != WhenTrace.Never && !(result?.Pass ?? false))
                {
                    StateTestTxTracer txTracer = new StateTestTxTracer();
                    txTracer.IsTracingMemory = _traceMemory;
                    txTracer.IsTracingStack = _traceStack;
                    result = RunTest(test, txTracer);

                    var txTrace = txTracer.BuildResult();
                    txTrace.Result.Time = result.TimeInMs;
                    txTrace.State.StateRoot = result.StateRoot;
                    txTrace.Result.GasUsed -= _calculator.Calculate(test.Transaction, test.Fork);
                    WriteErr(txTrace);    
                }
                
                results.Add(result);
            }
            
            WriteOut(results);

            Console.ReadLine();
            return results;
        }
    }
}