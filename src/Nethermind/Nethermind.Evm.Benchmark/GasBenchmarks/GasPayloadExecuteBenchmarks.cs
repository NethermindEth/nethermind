// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
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

        // Load genesis state once (shared across all test cases)
        PayloadLoader.EnsureGenesisInitialized(GasPayloadBenchmarks.s_genesisPath, pragueSpec);

        // Create a fresh WorldState and open scope at genesis
        _state = PayloadLoader.CreateWorldState();
        BlockHeader genesisBlock = new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 0, 0, 0, 0, Array.Empty<byte>())
        {
            StateRoot = PayloadLoader.GenesisStateRoot
        };
        _stateScope = _state.BeginScope(genesisBlock);

        // Set up EVM infrastructure
        TestBlockhashProvider blockhashProvider = new();
        EthereumCodeInfoRepository codeInfoRepo = new(_state);
        EthereumVirtualMachine vm = new(blockhashProvider, specProvider, LimboLogs.Instance);

        _txProcessor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            _state,
            vm,
            codeInfoRepo,
            LimboLogs.Instance);

        // Execute setup payload if one exists for this scenario
        string setupFile = GasPayloadBenchmarks.FindSetupFile(Scenario.FileName);
        if (setupFile is not null)
        {
            (BlockHeader setupHeader, Transaction[] setupTxs) = PayloadLoader.LoadPayload(setupFile);
            _txProcessor.SetBlockExecutionContext(setupHeader);
            for (int i = 0; i < setupTxs.Length; i++)
            {
                _txProcessor.Execute(setupTxs[i], NullTxTracer.Instance);
            }
            _state.Commit(pragueSpec);
        }

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

