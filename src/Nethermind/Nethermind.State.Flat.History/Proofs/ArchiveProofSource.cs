// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class ArchiveProofSource(
    IColumnsDb<FlatDbColumns> db,
    IColumnsDb<FlatHistoryColumns> history,
    HistoryReader historyReader,
    HistoryRowFormat rowFormat,
    CommitmentDepthPolicy policy,
    CommitmentMetadata metadata,
    ArchiveProofSettings settings,
    IFlatDbConfig config,
    ILogManager logManager) : IHistoricalTrieVisitor
{
    private const int NodeCacheCapacity = 100_000;

    private readonly ISortedKeyValueStore _accountRows = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.AccountHistory);
    private readonly ISortedKeyValueStore _storageRows = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageHistory);
    private readonly CommitmentStore _accountCommitments = new(history.GetColumnDb(FlatHistoryColumns.AccountCommitments));
    private readonly CommitmentStore _storageCommitments = new(history.GetColumnDb(FlatHistoryColumns.StorageCommitments));
    private readonly StorageClearStore _clears = new(history.GetColumnDb(FlatHistoryColumns.StorageClears));
    private readonly ArchiveProofNodeCache _nodeCache = new(NodeCacheCapacity);
    private readonly bool _rlpWrapSlots = BasePersistence.ResolveSlotEncoding(
        db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), logManager.GetClassLogger<ArchiveProofSource>());
    private readonly int _fanOut = config.ArchiveProofFanOut > 0 ? config.ArchiveProofFanOut : Environment.ProcessorCount;

    public bool Enabled => settings.ServeEnabled;

    public bool CanServe(in StateId stateId) =>
        Enabled
        && historyReader.IsAvailable(stateId)
        && metadata.TryReadStamp(policy, out bool stampMatches)
        && stampMatches
        && metadata.TryGetCoverage(out ulong from, out ulong to)
        && stateId.BlockNumber >= from
        && stateId.BlockNumber <= to;

    public bool TryRunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, in StateId stateId, VisitingOptions? visitingOptions, VisitingStats? diagnostics)
        where TCtx : struct, INodeContext<TCtx>
    {
        if (!CanServe(stateId)) return false;

        RunTreeVisitor(treeVisitor, stateId, visitingOptions, diagnostics);
        return true;
    }

    internal void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> visitor, in StateId stateId, VisitingOptions? visitingOptions, VisitingStats? diagnostics)
        where TCtx : struct, INodeContext<TCtx>
    {
        ResolutionBudget budget = new(config.ArchiveProofMaxScannedRows);
        PatriciaTree tree = new(CreateAccountStore(stateId.BlockNumber, budget), logManager);
        tree.Accept(visitor, stateId.StateRoot.ToCommitment(), visitingOptions, diagnostics: diagnostics);
    }

    private HistoricalTrieNodeBuilder CreateAccountBuilder(ulong block, ResolutionBudget budget) =>
        new(new AccountHistoryScope(_accountRows, rowFormat, _accountCommitments, policy), block, budget, _fanOut, _nodeCache);

    private HistoricalTrieNodeBuilder CreateStorageBuilder(in ValueHash256 accountPath, ulong block, ResolutionBudget budget) =>
        new(
            new StorageHistoryScope(_storageRows, rowFormat, _storageCommitments, policy, _clears, accountPath, _rlpWrapSlots),
            block, budget, _fanOut, _nodeCache);

    private ArchiveProofTrieStore CreateAccountStore(ulong block, ResolutionBudget budget) =>
        new(
            CreateAccountBuilder(block, budget),
            accountPath => new ArchiveProofTrieStore(CreateStorageBuilder(accountPath, block, budget), storageResolverFactory: null));
}
