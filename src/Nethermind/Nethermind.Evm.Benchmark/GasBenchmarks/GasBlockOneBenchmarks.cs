// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks that replay gas-benchmark payload files via BlockProcessor.ProcessOne.
/// This measures block processing without the BranchProcessor overhead (state scope management,
/// pre-warming coordination, cache clearing, TxHashCalculator).
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasBlockOneBenchmarks
{
    private IWorldState _state;
    private BlockProcessor _blockProcessor;
    private Block _testBlock;
    private BlockHeader _preBlockHeader;
    private IReleaseSpec _spec;
    private ProcessingOptions _processingOptions;

    [ParamsSource(nameof(GetTestCases))]
    public GasPayloadBenchmarks.TestCase Scenario { get; set; }

    public static IEnumerable<GasPayloadBenchmarks.TestCase> GetTestCases() => GasPayloadBenchmarks.GetTestCases();

    [GlobalSetup]
    public void GlobalSetup()
    {
        IReleaseSpec pragueSpec = Prague.Instance;
        ISpecProvider specProvider = new SingleReleaseSpecProvider(pragueSpec, 1, 1);
        _spec = pragueSpec;

        PayloadLoader.EnsureGenesisInitialized(GasPayloadBenchmarks.s_genesisPath, pragueSpec);

        _state = PayloadLoader.CreateWorldState();
        _preBlockHeader = BlockBenchmarkHelper.CreateGenesisHeader();

        TestBlockhashProvider blockhashProvider = new();
        ITransactionProcessor txProcessor = BlockBenchmarkHelper.CreateTransactionProcessor(
            _state, blockhashProvider, specProvider);

        BlockBenchmarkHelper.ExecuteSetupPayload(_state, txProcessor, _preBlockHeader, Scenario, specProvider);

        ReceiptConfig receiptConfig = new();
        IReceiptStorage receiptStorage = BlockBenchmarkHelper.CreateReceiptStorage(receiptConfig);
        _processingOptions = BlockBenchmarkHelper.GetImportProcessingOptions(receiptConfig);

        _blockProcessor = BlockBenchmarkHelper.CreateBlockProcessor(specProvider, txProcessor, _state, receiptStorage);
        _testBlock = PayloadLoader.LoadBlock(Scenario.FilePath);

        // Warm up and verify correctness
        using (IDisposable scope = _state.BeginScope(_preBlockHeader))
        {
            (Block processedBlock, _) = _blockProcessor.ProcessOne(
                _testBlock,
                _processingOptions,
                NullBlockTracer.Instance, _spec, default);
            _state.CommitTree(_preBlockHeader.Number + 1);
            PayloadLoader.VerifyProcessedBlock(processedBlock, Scenario.ToString(), Scenario.FilePath);
        }
    }

    [Benchmark]
    public void ProcessBlock()
    {
        using IDisposable scope = _state.BeginScope(_preBlockHeader);
        _blockProcessor.ProcessOne(
            _testBlock,
            _processingOptions,
            NullBlockTracer.Instance, _spec, default);
        _state.CommitTree(_preBlockHeader.Number + 1);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _state = null;
        _blockProcessor = null;
        _testBlock = null;
        _processingOptions = ProcessingOptions.None;
    }
}
