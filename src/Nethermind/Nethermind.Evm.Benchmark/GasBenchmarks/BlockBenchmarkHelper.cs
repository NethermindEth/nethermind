// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Shared setup helpers for gas benchmarks.
/// </summary>
internal static class BlockBenchmarkHelper
{
    public static BlocksConfig CreateBenchmarkBlocksConfig()
    {
        return new BlocksConfig
        {
            PreWarmStateOnBlockProcessing = true,
            // Keep this disabled in benchmarks to avoid hiding precompile-specific deltas.
            CachePrecompilesOnBlockProcessing = false
        };
    }

    public static BlockHeader CreateGenesisHeader() =>
        new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 0, 0, 0, 0, Array.Empty<byte>())
        {
            StateRoot = PayloadLoader.GenesisStateRoot
        };

    public static IStateReader CreateStateReader(IWorldState worldState) => new WorldStateReaderAdapter(worldState);

    public static ProcessingOptions GetNewPayloadProcessingOptions(IReceiptConfig receiptConfig) =>
        receiptConfig.StoreReceipts
            ? ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts
            : ProcessingOptions.EthereumMerge;

    public static ProcessingOptions GetBlockBuildingProcessingOptions(IBlocksConfig blocksConfig) =>
        blocksConfig.BuildBlocksOnMainState
            ? ProcessingOptions.NoValidation | ProcessingOptions.StoreReceipts | ProcessingOptions.DoNotUpdateHead
            : ProcessingOptions.ProducingBlock;

    public static BlockchainProcessor.Options GetBlockBuildingBlockchainProcessorOptions(IBlocksConfig blocksConfig) =>
        blocksConfig.BuildBlocksOnMainState ? BlockchainProcessor.Options.Default : BlockchainProcessor.Options.NoReceipts;

    /// <summary>
    /// Executes setup payload blocks (block-level overload) using a temporary BlockProcessor.
    /// Used by block-level benchmarks (BlockBuilding, NewPayload, NewPayloadMeasured).
    /// </summary>
    public static void ExecuteSetupPayload(
        IWorldState state, ITransactionProcessor txProcessor,
        BlockHeader preBlockHeader, GasPayloadBenchmarks.TestCase scenario,
        ISpecProvider specProvider)
    {
        string setupFile = GasPayloadBenchmarks.FindSetupFile(scenario.FileName);
        if (setupFile is null)
        {
            return;
        }

        Block[] setupBlocks = PayloadLoader.LoadAllSetupBlocks(setupFile);
        if (setupBlocks.Length == 0)
        {
            return;
        }

        BlockProcessor blockProcessor = CreateSetupBlockProcessor(specProvider, txProcessor, state);
        Block lastProcessed = null;

        using (state.BeginScope(preBlockHeader))
        {
            for (int i = 0; i < setupBlocks.Length; i++)
            {
                Block setupBlock = setupBlocks[i];
                IReleaseSpec spec = specProvider.GetSpec(setupBlock.Header);
                (lastProcessed, _) = blockProcessor.ProcessOne(
                    setupBlock, ProcessingOptions.None, NullBlockTracer.Instance, spec, default);
                state.CommitTree(setupBlock.Header.Number);
                state.Reset();
            }
        }

        preBlockHeader.StateRoot = lastProcessed.Header.StateRoot;
    }

    /// <summary>
    /// Executes a single setup payload via TransactionProcessor (tx-level overload).
    /// Used by tx-level benchmarks (EVMExecute).
    /// </summary>
    public static void ExecuteSetupPayload(
        IWorldState state,
        ITransactionProcessor txProcessor,
        GasPayloadBenchmarks.TestCase scenario,
        IReleaseSpec spec)
    {
        string setupFile = GasPayloadBenchmarks.FindSetupFile(scenario.FileName);
        if (setupFile is null)
        {
            return;
        }

        (BlockHeader setupHeader, Transaction[] setupTxs) = PayloadLoader.LoadPayload(setupFile);

        txProcessor.SetBlockExecutionContext(setupHeader);
        for (int i = 0; i < setupTxs.Length; i++)
        {
            txProcessor.Execute(setupTxs[i], NullTxTracer.Instance);
        }
        state.Commit(spec);
    }

    /// <summary>
    /// Creates a temporary BlockProcessor for setup payload processing only.
    /// Not used for measured benchmarks — those use DI-resolved components.
    /// </summary>
    private static BlockProcessor CreateSetupBlockProcessor(
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

    private sealed class WorldStateReaderAdapter(IWorldState worldState) : IStateReader
    {
        public bool TryGetAccount(BlockHeader baseBlock, Address address, out AccountStruct account)
        {
            account = default;
            return false;
        }

        public ReadOnlySpan<byte> GetStorage(BlockHeader baseBlock, Address address, in UInt256 index) => [];

        public byte[] GetCode(Hash256 codeHash) => null;

        public byte[] GetCode(in ValueHash256 codeHash) => null;

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader baseBlock, VisitingOptions visitingOptions = null)
            where TCtx : struct, INodeContext<TCtx>
        {
        }

        public bool HasStateForBlock(BlockHeader baseBlock) => worldState.HasStateForBlock(baseBlock);
    }
}
