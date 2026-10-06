// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Proofs;
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
    private readonly CommitmentStore _accountCommitments = new(history.GetColumnDb(FlatHistoryColumns.AccountCommitments), policy, 0);
    private readonly CommitmentStore _storageCommitments = new(history.GetColumnDb(FlatHistoryColumns.StorageCommitments), policy, CommitmentKeyLayout.IdentityLength);
    private readonly StorageClearStore _clears = new(history.GetColumnDb(FlatHistoryColumns.StorageClears));
    private readonly Lazy<ArchiveProofNodeCache> _nodeCache = new(() => new ArchiveProofNodeCache(NodeCacheCapacity));
    private readonly Lazy<bool> _rlpWrapSlots = new(() => BasePersistence.ResolveSlotEncoding(
        db, (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage), logManager.GetClassLogger<ArchiveProofSource>()));
    private readonly int _fanOut = config.ArchiveProofFanOut > 0 ? config.ArchiveProofFanOut : Environment.ProcessorCount;

    public bool Enabled => settings.ServeEnabled;

    internal bool ServingResourcesCreated => _nodeCache.IsValueCreated || _rlpWrapSlots.IsValueCreated;

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
        if (treeVisitor is not AccountProofCollector collector || !CanServe(stateId)) return false;

        CollectProof(collector, stateId, diagnostics);
        return true;
    }

    internal void CollectProof(AccountProofCollector collector, in StateId stateId, VisitingStats? diagnostics)
    {
        ResolutionBudget budget = new(config.ArchiveProofMaxScannedRows, collector.CancellationToken);
        ulong block = stateId.BlockNumber;
        ulong minEpoch = metadata.DroppedThroughEpoch;
        ValueHash256 identity = collector.HashedAddress;
        bool prefetch = !_nodeCache.Value.TryGet(stateId.StateRoot, out _);
        HistoricalTrieNodeBuilder accounts = CreateAccountBuilder(block, budget, minEpoch);
        if (prefetch)
        {
            HashSet<(HistoricalTrieNodeBuilder Builder, TreePath Path)> work = [];
            accounts.CollectPrefetch(identity, work);
            HistoricalTrieNodeBuilder.Prefetch([.. work], accounts.FanOutOptions);
        }

        if (!ArchiveProofPathWalker.TryProveAccount(collector, accounts, stateId.StateRoot, diagnostics, out AccountStruct account)) return;

        ValueHash256[] slots = collector.GetHashedStorageKeys();
        if (slots.Length == 0 || !account.HasStorage) return;

        HistoricalTrieNodeBuilder storage = CreateStorageBuilder(identity, block, budget, minEpoch);
        if (prefetch)
        {
            storage.PrefetchOne(TreePath.Empty);
            HashSet<(HistoricalTrieNodeBuilder Builder, TreePath Path)> deeper = [];
            foreach (ValueHash256 slot in slots) storage.CollectPrefetch(slot, deeper, fromDepth: 1);
            HistoricalTrieNodeBuilder.Prefetch([.. deeper], storage.FanOutOptions);
        }

        for (int index = 0; index < slots.Length; index++)
        {
            ArchiveProofPathWalker.ProveSlot(collector, storage, account.StorageRoot, slots[index], index, diagnostics);
        }
    }

    private HistoricalTrieNodeBuilder CreateAccountBuilder(ulong block, ResolutionBudget budget, ulong minEpoch) =>
        new(new AccountHistoryScope(_accountRows, rowFormat, _accountCommitments, policy) { MinEpoch = minEpoch, MinEpochSource = DroppedThrough, FineMinEpochSource = DemotedThrough }, block, budget, _fanOut, _nodeCache.Value);

    private HistoricalTrieNodeBuilder CreateStorageBuilder(in ValueHash256 accountPath, ulong block, ResolutionBudget budget, ulong minEpoch) =>
        new(
            new StorageHistoryScope(_storageRows, rowFormat, _storageCommitments, metadata, policy, _clears, accountPath, _rlpWrapSlots.Value) { MinEpoch = minEpoch, MinEpochSource = DroppedThrough, FineMinEpochSource = DemotedThrough },
            block, budget, _fanOut, _nodeCache.Value);

    private ulong DroppedThrough() => metadata.DroppedThroughEpoch;

    private ulong DemotedThrough() => metadata.DemotedThroughEpoch;
}
