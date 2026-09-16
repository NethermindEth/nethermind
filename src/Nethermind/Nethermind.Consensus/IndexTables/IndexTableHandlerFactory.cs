// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.IndexTables;

/// <inheritdoc cref="IIndexTableHandlerFactory"/>
public sealed class IndexTableHandlerFactory(
    IIndexTableStore store,
    ISpecProvider specProvider,
    IBlockTree? blockTree = null,
    IReceiptStorage? receiptStorage = null,
    ILogManager? logManager = null) : IIndexTableHandlerFactory
{
    public static IndexTableHandlerFactory Default { get; } = new(new IndexTableStore(), new FallbackSpecProvider());

    public IIndexTableStore Store => store;

    /// <inheritdoc />
    public IIndexTableHandler Create(ITransactionProcessor transactionProcessor) =>
        new IndexTableHandler(transactionProcessor, store, specProvider, blockTree, receiptStorage, logManager);

    /// <summary>
    /// Minimal spec provider used only by the <see cref="Default"/> instance, which exists
    /// for backward compatibility with code paths that don't inject a real provider.
    /// Returns specs where EIP-8304 is disabled, so no index work runs.
    /// </summary>
    private sealed class FallbackSpecProvider : ISpecProvider
    {
        public void UpdateMergeTransitionInfo(ulong? blockNumber, Nethermind.Int256.UInt256? terminalTotalDifficulty = null) { }
        public ForkActivation? MergeBlockNumber => null;
        public ulong TimestampFork => ISpecProvider.TimestampForkNever;
        public Nethermind.Int256.UInt256? TerminalTotalDifficulty => null;
        public ulong? BeaconChainGenesisTimestamp { get; set; }
        public ulong NetworkId => 0;
        public ulong ChainId => 0;
        public ulong? DaoBlockNumber => null;
        public ForkActivation[] TransitionActivations => [];
        public IReleaseSpec GenesisSpec => Nethermind.Specs.Forks.Frontier.Instance;
        public IReleaseSpec GetSpec(ForkActivation forkActivation) => Nethermind.Specs.Forks.Frontier.Instance;
    }
}
