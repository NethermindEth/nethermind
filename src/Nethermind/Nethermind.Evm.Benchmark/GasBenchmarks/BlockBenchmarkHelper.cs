// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Shared setup helpers for block-level gas benchmarks (BlockOne, Block, NewPayload modes).
/// </summary>
internal static class BlockBenchmarkHelper
{
    public static BlockHeader CreateGenesisHeader() =>
        new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 0, 0, 0, 0, Array.Empty<byte>())
        {
            StateRoot = PayloadLoader.GenesisStateRoot
        };

    public static ITransactionProcessor CreateTransactionProcessor(
        IWorldState state, IBlockhashProvider blockhashProvider, ISpecProvider specProvider)
    {
        EthereumCodeInfoRepository codeInfoRepo = new(state);
        EthereumVirtualMachine vm = new(blockhashProvider, specProvider, LimboLogs.Instance);

        return new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance, specProvider, state, vm, codeInfoRepo, LimboLogs.Instance);
    }

    public static BlockProcessor CreateBlockProcessor(
        ISpecProvider specProvider, ITransactionProcessor txProcessor, IWorldState state) =>
        new(specProvider,
            Always.Valid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProcessor), state),
            state,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, state),
            new BlockhashStore(state),
            LimboLogs.Instance,
            new WithdrawalProcessor(state, LimboLogs.Instance),
            new ExecutionRequestsProcessor(txProcessor));

    public static void ExecuteSetupPayload(
        IWorldState state, ITransactionProcessor txProcessor,
        BlockHeader preBlockHeader, GasPayloadBenchmarks.TestCase scenario,
        IReleaseSpec spec)
    {
        string setupFile = GasPayloadBenchmarks.FindSetupFile(scenario.FileName);
        if (setupFile is null) return;

        using IDisposable setupScope = state.BeginScope(preBlockHeader);
        (BlockHeader setupHeader, Transaction[] setupTxs) = PayloadLoader.LoadPayload(setupFile);
        txProcessor.SetBlockExecutionContext(setupHeader);
        for (int i = 0; i < setupTxs.Length; i++)
        {
            txProcessor.Execute(setupTxs[i], NullTxTracer.Instance);
        }
        state.Commit(spec);
        state.CommitTree(preBlockHeader.Number);
        preBlockHeader.StateRoot = state.StateRoot;
    }
}
