// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
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
        private readonly bool _suppressOutput;
        private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

        public StateTestsRunner(ITestSourceLoader testsSource, WhenTrace whenTrace, bool traceMemory, bool traceStack, ulong chainId, string? filter = null, bool enableWarmup = false, bool suppressOutput = false)
        {
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));
            _whenTrace = whenTrace;
            _traceMemory = traceMemory;
            _traceStack = traceStack;
            _filter = filter;
            _chainId = chainId;
            _enableWarmup = enableWarmup;
            _suppressOutput = suppressOutput;
            Setup(null);
        }

        private void WriteOut(List<EthereumTestResult> testResult)
        {
            if (!_suppressOutput)
                Console.Out.Write(_serializer.Serialize(testResult, true));
        }

        private void WriteErr(StateTestTxTrace txTrace)
        {
            // Emit each opcode step as an EIP-3155 JSON line to stderr.
            foreach (var entry in txTrace.Entries)
            {
                var stackJson = string.Join(",", entry.Stack.Select(s => $"\"{s}\""));
                Console.Error.Write($"{{\"pc\":{entry.Pc},\"op\":{entry.Operation},\"gas\":\"0x{entry.Gas:x}\",\"gasCost\":\"0x{entry.GasCost:x}\",\"stack\":[{stackJson}],\"depth\":{entry.Depth},\"memSize\":{entry.MemSize}");
                if (!string.IsNullOrEmpty(entry.Error))
                    Console.Error.Write($",\"error\":\"{entry.Error}\"");
                Console.Error.WriteLine("}");
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

        public EthereumTestResult RunSingleTest(GeneralStateTest test)
        {
            test.ChainId = _chainId;

            if (_whenTrace == WhenTrace.Always)
            {
                StateTestTxTracer txTracer = new();
                txTracer.IsTracingDetailedMemory = _traceMemory;
                // EIP-3155 always needs stack; IsTracingStack controls whether
                // the EVM calls SetOperationStack at all.
                txTracer.IsTracingStack = true;
                var result = RunTest(test, txTracer);

                var txTrace = txTracer.BuildResult();
                txTrace.Result.Time = result.TimeInMs;
                txTrace.State.StateRoot = result.StateRoot;
                txTrace.Result.GasUsed -= IntrinsicGasCalculator.Calculate(test.Transaction, test.Fork).Standard;
                WriteErr(txTrace);
                return result;
            }

            return RunTest(test, NullTxTracer.Instance);
        }
    }
}
