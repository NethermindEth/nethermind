// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Json;

namespace Nethermind.Test.Runner
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
        private readonly string? _filter;
        private readonly ulong _chainId;
        private readonly bool _enableWarmup;
        private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

        public StateTestsRunner(ITestSourceLoader testsSource, WhenTrace whenTrace, bool traceMemory, bool traceStack, ulong chainId, string? filter = null, bool enableWarmup = false)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
            _whenTrace = whenTrace;
            _traceMemory = traceMemory;
            _traceStack = traceStack;
            _filter = filter;
            _chainId = chainId;
            _enableWarmup = enableWarmup;
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
            IEnumerable<GeneralStateTest> tests = _testsSource.LoadTests<GeneralStateTest>();
            foreach (GeneralStateTest test in tests)
            {
                if (_filter is not null && !Regex.Match(test.Name, $"^({_filter})").Success)
                    continue;
                test.ChainId = _chainId;

                EthereumTestResult result = null;
                if (_whenTrace != WhenTrace.Always)
                {
                    if (_enableWarmup)
                    {
                        // Warm up only when benchmarking
                        Parallel.For(0, 30, (i, s) =>
                        {
                            _ = RunTest(test, NullTxTracer.Instance);
                        });

                        // Give time to Jit optimized version
                        Thread.Sleep(20);
                        GC.Collect(GC.MaxGeneration);
                    }
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
                    txTrace.Result.GasUsed -= IntrinsicGasCalculator.Calculate(test.Transaction, test.Fork).Standard;
                    WriteErr(txTrace);
                }

                results.Add(result);
            }

            WriteOut(results);

            return results;
        }
    }
}
