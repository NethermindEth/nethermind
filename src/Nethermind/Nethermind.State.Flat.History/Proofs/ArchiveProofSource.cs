// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
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
    private long _prefetchedPaths;

    public bool Enabled => settings.ServeEnabled;

    internal bool ServingResourcesCreated => _nodeCache.IsValueCreated || _rlpWrapSlots.IsValueCreated;

    internal long PrefetchedPaths => Interlocked.Read(ref _prefetchedPaths);

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
        if (treeVisitor is not AccountProofCollector || !CanServe(stateId)) return false;

        RunTreeVisitor(treeVisitor, stateId, visitingOptions, diagnostics);
        return true;
    }

    internal void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> visitor, in StateId stateId, VisitingOptions? visitingOptions, VisitingStats? diagnostics)
        where TCtx : struct, INodeContext<TCtx>
    {
        AccountProofCollector? collector = visitor as AccountProofCollector;
        ResolutionBudget budget = new(config.ArchiveProofMaxScannedRows, collector?.CancellationToken ?? default);
        ulong minEpoch = metadata.DroppedThroughEpoch;
        ArchiveProofTrieStore store = collector is not null
            ? CreatePrefetchedStore(collector, stateId.StateRoot, stateId.BlockNumber, budget, minEpoch)
            : CreateAccountStore(stateId.BlockNumber, budget, minEpoch);
        PatriciaTree tree = new(store, logManager);
        tree.Accept(visitor, stateId.StateRoot.ToCommitment(), visitingOptions, diagnostics: diagnostics);
    }

    private ArchiveProofTrieStore CreatePrefetchedStore(AccountProofCollector collector, ValueHash256 stateRoot, ulong block, ResolutionBudget budget, ulong minEpoch)
    {
        HistoricalTrieNodeBuilder accounts = CreateAccountBuilder(block, budget, minEpoch);
        ValueHash256 identity = collector.HashedAddress;
        HistoricalTrieNodeBuilder storage = CreateStorageBuilder(identity, block, budget, minEpoch);
        ArchiveProofTrieStore storageStore = new(storage, storageResolverFactory: null);

        int accountDepth = _nodeCache.Value.FirstUncachedDepth(stateRoot, identity, out _);
        if (accountDepth != ArchiveProofNodeCache.PathCached)
        {
            HashSet<(HistoricalTrieNodeBuilder Builder, TreePath Path)> work = [];
            accounts.CollectPrefetch(identity, work, accountDepth);
            Prefetch(work, accounts.FanOutOptions);
        }

        ValueHash256[] slots = collector.GetHashedStorageKeys();
        bool storagePrefetched = false;
        return new ArchiveProofTrieStore(
            accounts,
            accountPath =>
            {
                if (accountPath != identity) return new ArchiveProofTrieStore(CreateStorageBuilder(accountPath, block, budget, minEpoch), storageResolverFactory: null);

                if (slots.Length > 0 && !storagePrefetched)
                {
                    storagePrefetched = true;
                    PrefetchStorage(storage, stateRoot, identity, slots);
                }

                return storageStore;
            });
    }

    private void PrefetchStorage(HistoricalTrieNodeBuilder storage, in ValueHash256 stateRoot, in ValueHash256 identity, ValueHash256[] slots)
    {
        ArchiveProofNodeCache cache = _nodeCache.Value;
        bool storageRootKnown = TryReadCachedStorageRoot(cache, stateRoot, identity, out ValueHash256 storageRoot);
        HashSet<(HistoricalTrieNodeBuilder Builder, TreePath Path)> deeper = [];
        foreach (ValueHash256 slot in slots)
        {
            int fromDepth = storageRootKnown ? cache.FirstUncachedDepth(storageRoot, slot, out _) : 0;
            if (fromDepth != ArchiveProofNodeCache.PathCached) storage.CollectPrefetch(slot, deeper, Math.Max(1, fromDepth));
        }

        if (deeper.Count == 0) return;

        storage.PrefetchOne(TreePath.Empty);
        Prefetch(deeper, storage.FanOutOptions);
    }

    private static bool TryReadCachedStorageRoot(ArchiveProofNodeCache cache, in ValueHash256 stateRoot, in ValueHash256 identity, out ValueHash256 storageRoot)
    {
        storageRoot = default;
        if (cache.FirstUncachedDepth(stateRoot, identity, out byte[]? leaf) != ArchiveProofNodeCache.PathCached || leaf is null) return false;

        RlpReader reader = new(leaf);
        reader.ReadSequenceLength();
        reader.SkipItem();
        if (!AccountDecoder.Instance.TryDecodeStruct(reader.DecodeByteArraySpan(), out AccountStruct account)) return false;

        storageRoot = account.StorageRoot;
        return true;
    }

    private void Prefetch(HashSet<(HistoricalTrieNodeBuilder Builder, TreePath Path)> work, ParallelOptions options)
    {
        if (work.Count == 0) return;

        Interlocked.Add(ref _prefetchedPaths, work.Count);
        HistoricalTrieNodeBuilder.Prefetch([.. work], options);
    }

    private HistoricalTrieNodeBuilder CreateAccountBuilder(ulong block, ResolutionBudget budget, ulong minEpoch) =>
        new(new AccountHistoryScope(_accountRows, rowFormat, _accountCommitments, policy) { MinEpoch = minEpoch, MinEpochSource = DroppedThrough, FineMinEpochSource = DemotedThrough }, block, budget, _fanOut, _nodeCache.Value);

    private HistoricalTrieNodeBuilder CreateStorageBuilder(in ValueHash256 accountPath, ulong block, ResolutionBudget budget, ulong minEpoch) =>
        new(
            new StorageHistoryScope(_storageRows, rowFormat, _storageCommitments, metadata, policy, _clears, accountPath, _rlpWrapSlots.Value) { MinEpoch = minEpoch, MinEpochSource = DroppedThrough, FineMinEpochSource = DemotedThrough },
            block, budget, _fanOut, _nodeCache.Value);

    private ulong DroppedThrough() => metadata.DroppedThroughEpoch;

    private ulong DemotedThrough() => metadata.DemotedThroughEpoch;

    private ArchiveProofTrieStore CreateAccountStore(ulong block, ResolutionBudget budget, ulong minEpoch) =>
        new(
            CreateAccountBuilder(block, budget, minEpoch),
            accountPath => new ArchiveProofTrieStore(CreateStorageBuilder(accountPath, block, budget, minEpoch), storageResolverFactory: null));
}
