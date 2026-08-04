// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        private readonly ITestSourceLoader? _testsSource;
        private readonly WhenTrace _whenTrace;
        private readonly bool _traceMemory;
        private readonly bool _traceStack;
        private readonly string? _filter;
        private readonly ulong _chainId;
        private readonly bool _enableWarmup;
        private readonly bool _suppressOutput;
        private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

        /// <summary>Creates a runner for <see cref="RunSingleTest"/> callers that supply tests directly.</summary>
        public StateTestsRunner(WhenTrace whenTrace, bool traceMemory, bool traceStack, ulong chainId, string? filter = null, bool enableWarmup = false, bool suppressOutput = false)
        {
            _whenTrace = whenTrace;
            _traceMemory = traceMemory;
            _traceStack = traceStack;
            _filter = filter;
            _chainId = chainId;
            _enableWarmup = enableWarmup;
            _suppressOutput = suppressOutput;
            Setup(null);
        }

        public StateTestsRunner(ITestSourceLoader testsSource, WhenTrace whenTrace, bool traceMemory, bool traceStack, ulong chainId, string? filter = null, bool enableWarmup = false, bool suppressOutput = false)
            : this(whenTrace, traceMemory, traceStack, chainId, filter, enableWarmup, suppressOutput) =>
            _testsSource = testsSource ?? throw new ArgumentNullException(nameof(testsSource));

        private void WriteOut(List<EthereumTestResult> testResult)
        {
            if (!_suppressOutput)
                Console.Out.Write(_serializer.Serialize(testResult, true));
        }

        private void WriteErr(StateTestTxTrace txTrace)
        {
            foreach (StateTestTxTraceEntry entry in txTrace.Entries)
            {
                string stackJson = BuildStackJson(entry.Stack);
                Console.Error.Write($"{{\"pc\":{entry.Pc},\"op\":{entry.Operation},\"gas\":\"0x{entry.Gas:x}\",\"gasCost\":\"0x{entry.GasCost:x}\",\"stack\":[{stackJson}],\"depth\":{entry.Depth},\"memSize\":{entry.MemSize}");
                if (!string.IsNullOrEmpty(entry.Error))
                    Console.Error.Write($",\"error\":{System.Text.Json.JsonSerializer.Serialize(entry.Error)}");
                Console.Error.WriteLine("}");
            }

            Console.Error.WriteLine(_serializer.Serialize(txTrace.Result));
            Console.Error.WriteLine(_serializer.Serialize(txTrace.State));
        }

        private static string BuildStackJson(List<string> stack)
        {
            StringBuilder builder = new();
            for (int i = 0; i < stack.Count; i++)
            {
                if (i != 0)
                    builder.Append(',');

                // Serialize adds the quotes and escapes the value; stack entries are hex
                // literals today, but a bare append would break on any future quote/backslash.
                builder.Append(System.Text.Json.JsonSerializer.Serialize(stack[i]));
            }

            return builder.ToString();
        }

        public IEnumerable<EthereumTestResult> RunTests()
        {
            if (_testsSource is null)
                throw new InvalidOperationException("RunTests requires a test source; use the constructor that accepts ITestSourceLoader.");

            List<EthereumTestResult> results = [];
            IEnumerable<GeneralStateTest> tests = _testsSource.LoadTests<GeneralStateTest>();
            foreach (GeneralStateTest test in tests)
            {
                if (_filter is not null && !Regex.Match(test.Name, $"^({_filter})").Success)
                    continue;
                test.ChainId = _chainId;

                results.Add(ExecuteWithOptionalTrace(test));
            }

            WriteOut(results);

            return results;
        }

        public EthereumTestResult RunSingleTest(GeneralStateTest test)
        {
            test.ChainId = _chainId;
            return ExecuteWithOptionalTrace(test);
        }

        private EthereumTestResult ExecuteWithOptionalTrace(GeneralStateTest test)
        {
            EthereumTestResult? result = null;
            if (_whenTrace != WhenTrace.Always)
            {
                WarmUp(test);
                result = RunTest(test, NullTxTracer.Instance);
            }

            if (_whenTrace != WhenTrace.Never && !(result?.Pass ?? false))
            {
                StateTestTxTracer txTracer = new();
                txTracer.IsTracingDetailedMemory = _traceMemory;
                // EIP-3155 always needs stack; IsTracingStack controls whether
                // the EVM calls SetOperationStack at all.
                txTracer.IsTracingStack = _traceStack;
                result = RunTest(test, txTracer);

                StateTestTxTrace txTrace = txTracer.BuildResult();
                txTrace.Result.Time = result.TimeInMs;
                txTrace.State.StateRoot = result.StateRoot;
                try
                {
                    txTrace.Result.GasUsed -= IntrinsicGasCalculator.Calculate(test.Transaction, test.Fork).Standard;
                }
                catch (InvalidDataException e)
                {
                    _logger.Info($"Skipping intrinsic-gas trace adjustment for {test.Name}: {e.Message}");
                }
                WriteErr(txTrace);
            }

            return result!;
        }

        private void WarmUp(GeneralStateTest test)
        {
            if (!_enableWarmup)
                return;

            // Warm up only when benchmarking.
            Parallel.For(0, 30, (i, s) =>
            {
                _ = RunTest(test, NullTxTracer.Instance);
            });

            // Give time to JIT optimized version.
            Thread.Sleep(20);
            GC.Collect(GC.MaxGeneration);
        }
    }
}
