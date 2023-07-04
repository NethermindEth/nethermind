// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
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

    public class StateTestsRunner : GeneralStateTestBase, IStateTestRunner
    {
        private readonly ITestSourceLoader _testsSource;
        private readonly WhenTrace _whenTrace;
        private readonly bool _traceMemory;
        private readonly bool _traceStack;
        private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

        public StateTestsRunner(ITestSourceLoader testsSource, WhenTrace whenTrace, bool traceMemory, bool traceStack)
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

        public IEnumerable<EthereumTestResult> RunTests()
        {
            List<EthereumTestResult> results = new();
            IEnumerable<GeneralStateTest> tests = (IEnumerable<GeneralStateTest>)_testsSource.LoadTests();
            foreach (GeneralStateTest test in tests)
            {
                EthereumTestResult result = null;
                if (_whenTrace != WhenTrace.Always)
                {
                    result = RunTest(test, NullTxTracer.Instance);
                }

                if (_whenTrace != WhenTrace.Never && !(result?.Pass ?? false))
                {
                    StateTestTxTracer txTracer = new();
                    txTracer.IsTracingDetailedMemory = _traceMemory;
                    txTracer.IsTracingStack = _traceStack;
                    result = RunTest(test, txTracer);

                    var txTrace = txTracer.BuildResult();
                    txTrace.Result.Time = result.TimeInMs;
                    txTrace.State.StateRoot = result.StateRoot;
                    txTrace.Result.GasUsed -= IntrinsicGasCalculator.Calculate(test.Transaction, test.Fork);
                    WriteErr(txTrace);
                }

                results.Add(result);
            }

            WriteOut(results);

            return results;
        }
    }
}
