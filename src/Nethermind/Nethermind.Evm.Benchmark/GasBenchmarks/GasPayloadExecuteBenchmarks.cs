// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks that replay gas-benchmark payload files via TransactionProcessor.Execute.
/// This follows the validation/import execution path (as used for normal block processing),
/// while keeping the harness focused on transaction execution itself.
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasPayloadExecuteBenchmarks
{
    private IWorldState _state;
    private IDisposable _stateScope;
    private ITransactionProcessor _txProcessor;
    private Transaction[] _testTransactions;
    private BlockHeader _testHeaderTemplate;

    [ParamsSource(nameof(GetTestCases))]
    public GasPayloadBenchmarks.TestCase Scenario { get; set; }

    public static IEnumerable<GasPayloadBenchmarks.TestCase> GetTestCases() => GasPayloadBenchmarks.GetTestCases();

    [GlobalSetup]
    public void GlobalSetup()
    {
        IReleaseSpec pragueSpec = Prague.Instance;
        ISpecProvider specProvider = new SingleReleaseSpecProvider(pragueSpec, 1, 1);

        BlockBenchmarkHelper.TransactionProcessingContext context = BlockBenchmarkHelper.CreateTransactionProcessingContext(specProvider, pragueSpec);
        _state = context.State;
        _stateScope = context.StateScope;
        _txProcessor = context.TransactionProcessor;

        BlockBenchmarkHelper.ExecuteSetupPayload(_state, _txProcessor, Scenario, pragueSpec);

        // Parse the test payload
        (BlockHeader header, Transaction[] txs) = PayloadLoader.LoadPayload(Scenario.FilePath);
        _testHeaderTemplate = header;
        _testTransactions = txs;

        // Warm up once on Execute path, then restore state.
        ExecutePayloadCore();
        _state.Reset();
    }

    [Benchmark]
    public void ExecutePayload()
    {
        ExecutePayloadCore();
        _state.Reset();
    }

    private void ExecutePayloadCore()
    {
        BlockHeader executionHeader = _testHeaderTemplate.Clone();
        executionHeader.GasUsed = 0;
        _txProcessor.SetBlockExecutionContext(executionHeader);
        for (int i = 0; i < _testTransactions.Length; i++)
        {
            _txProcessor.Execute(_testTransactions[i], NullTxTracer.Instance);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _stateScope?.Dispose();
        _stateScope = null;
        _state = null;
        _txProcessor = null;
        _testTransactions = null;
        _testHeaderTemplate = null;
    }
}
