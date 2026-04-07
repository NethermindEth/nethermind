// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core.Crypto;
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
        private readonly bool _dumpState;
        private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

        public StateTestsRunner(
            ITestSourceLoader testsSource,
            WhenTrace whenTrace,
            bool traceMemory,
            bool traceStack,
            ulong chainId,
            string? filter = null,
            bool enableWarmup = false,
            bool dumpState = false)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
            _whenTrace = whenTrace;
            _traceMemory = traceMemory;
            _traceStack = traceStack;
            _filter = filter;
            _chainId = chainId;
            _enableWarmup = enableWarmup;
            _dumpState = dumpState;
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
            Action<string, string, long, Hash256, string>? stateDumper = _dumpState ? WriteStateDump : null;
            foreach (GeneralStateTest test in tests)
            {
                if (_filter is not null && !Regex.Match(test.Name, $"^({_filter})").Success)
                    continue;
                test.ChainId = _chainId;

                EthereumTestResult result;

                if (_whenTrace == WhenTrace.Never)
                {
                    result = RunTest(test, NullTxTracer.Instance, stateDumper);
                }
                else if (_whenTrace == WhenTrace.Always)
                {
                    StateTestTxTracer txTracer = new();
                    txTracer.IsTracingDetailedMemory = _traceMemory;
                    txTracer.IsTracingStack = _traceStack;
                    result = RunTest(test, txTracer, stateDumper);

                    var txTrace = txTracer.BuildResult();
                    txTrace.Result.Time = result.TimeInMs;
                    txTrace.State.StateRoot = result.StateRoot;
                    txTrace.Result.GasUsed -= IntrinsicGasCalculator.Calculate(test.Transaction, test.Fork).Standard;
                    WriteErr(txTrace);
                }
                else
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

                    result = RunTest(test, NullTxTracer.Instance, stateDumper);

                    if (!result.Pass)
                    {
                        StateTestTxTracer txTracer = new();
                        txTracer.IsTracingDetailedMemory = _traceMemory;
                        txTracer.IsTracingStack = _traceStack;
                        result = RunTest(test, txTracer, stateDumper);

                        var txTrace = txTracer.BuildResult();
                        txTrace.Result.Time = result.TimeInMs;
                        txTrace.State.StateRoot = result.StateRoot;
                        txTrace.Result.GasUsed -= IntrinsicGasCalculator.Calculate(test.Transaction, test.Fork).Standard;
                        WriteErr(txTrace);
                    }
                }

                results.Add(result);
            }

            WriteOut(results);

            return results;
        }

        private void WriteStateDump(string testName, string phase, long blockNumber, Hash256 stateRoot, string state)
        {
            StateDumpEnvelope envelope = new()
            {
                StateDump = new StateDumpPayload
                {
                    Name = testName,
                    Phase = phase,
                    Block = blockNumber,
                    StateRoot = stateRoot,
                    State = state
                }
            };

            Console.Error.WriteLine(_serializer.Serialize(envelope));
        }

        private sealed class StateDumpEnvelope
        {
            public required StateDumpPayload StateDump { get; init; }
        }

        private sealed class StateDumpPayload
        {
            public required string Name { get; init; }
            public required string Phase { get; init; }
            public required long Block { get; init; }
            public required Hash256 StateRoot { get; init; }
            public required string State { get; init; }
        }
    }
}
