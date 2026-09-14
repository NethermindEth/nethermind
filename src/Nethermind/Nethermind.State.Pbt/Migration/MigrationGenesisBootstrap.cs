// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using FlatStateId = Nethermind.State.Flat.StateId;
using Snapshot = Nethermind.State.Flat.Snapshot;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Materializes the MPT genesis allocation into an isolated preimage-flat source for offline conversion.</summary>
/// <remarks>
/// The node's own genesis goes through main processing like any block; this builds it a second time into the
/// source database so the anchor artifacts can be produced from a preimage layout, and checks both agree. The
/// genesis builder drops the allocations once it has applied them, so they are captured while it runs.
/// </remarks>
internal sealed class MigrationGenesisBootstrap(
    MigrationGenesisSource source,
    ChainSpec chainSpec,
    ILifetimeScope rootLifetime,
    IBlockTree blockTree,
    IResourcePool resourcePool,
    IFlatDbConfig configuration,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ILogManager logManager) : IFlatCommitTarget, IGenesisPostProcessor
{
    private Dictionary<Address, ChainSpecAllocation>? _allocations;
    private int _committed;

    public MigrationGenesisSource Source => source;

    public void PostProcess(Block genesis) => _allocations ??= chainSpec.Allocations;

    public void EnsureSource(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlockHeader genesis = blockTree.Genesis ?? throw new InvalidDataException("Genesis bootstrap requires the loaded genesis header.");
        using (IPersistence.IPersistenceReader reader = source.Persistence.CreateReader())
        {
            if (reader.CurrentState == new FlatStateId(genesis)) return;
            if (reader.CurrentState != FlatStateId.PreGenesis)
                throw new InvalidDataException("The existing genesis source belongs to a different state.");
        }

        Hash256 expectedHash = genesis.Hash!;
        chainSpec.Allocations = _allocations ?? chainSpec.Allocations
            ?? throw new InvalidDataException("Genesis bootstrap requires the genesis allocation; the genesis was built without this bootstrap.");
        GenesisScopeProvider provider = new(this);
        using ILifetimeScope scope = rootLifetime.BeginLifetimeScope(builder =>
            builder.RegisterInstance(provider).As<IWorldStateScopeProvider>().ExternallyOwned());
        IWorldState state = scope.Resolve<IWorldState>();
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            Block built = scope.Resolve<IGenesisBuilder>().Build();
            if (built.Hash != expectedHash)
                throw new InvalidDataException($"Genesis source {built.Hash} differs from the loaded genesis {expectedHash}.");
        }
        if (Volatile.Read(ref _committed) == 0) throw new InvalidDataException("Genesis bootstrap committed no state.");
    }

    private FlatWorldStateScope Open()
    {
        IPersistence.IPersistenceReader reader = source.Persistence.CreateReader();
        ReadOnlySnapshotBundle readOnly = new(new SnapshotPooledList(0), reader, false, PersistedSnapshotStack.Empty(), isHistorical: false);
        SnapshotPooledList snapshots = new(0);
        SnapshotBundle bundle;
        try
        {
            bundle = new SnapshotBundle(readOnly, new EmptyTrieNodeCache(), resourcePool, ResourcePool.Usage.ReadOnlyProcessingEnv, snapshots);
        }
        catch
        {
            try { snapshots.Dispose(); }
            finally { readOnly.Dispose(); }
            throw;
        }
        try
        {
            return new FlatWorldStateScope(FlatStateId.PreGenesis, bundle,
                new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(codeDb), this, configuration, new NoopTrieWarmer(), logManager);
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
    {
        try
        {
            if (snapshot.From != FlatStateId.PreGenesis || snapshot.To.BlockNumber != 0 || Interlocked.CompareExchange(ref _committed, 1, 0) != 0)
                throw new InvalidOperationException("Only one genesis commit is permitted.");
            // Code batches precede CommitTree; make them durable before the source state metadata.
            codeDb.SyncWal();
            Persist(source.Persistence, snapshot);
            source.Database.SyncWal();
        }
        finally
        {
            try { snapshot.Dispose(); }
            finally { transientResource.ReleaseLease(); }
        }
    }

    private static void Persist(IPersistence target, Snapshot snapshot)
    {
        using IPersistence.IWriteBatch batch = target.CreateWriteBatch(snapshot.From, snapshot.To);
        foreach ((HashedKey<Address> address, bool isNew) in snapshot.SelfDestructedStorageAddresses)
            if (!isNew) batch.SelfDestruct(address.Key);
        foreach ((HashedKey<Address> address, Account? account) in snapshot.Accounts)
            batch.SetAccount(address.Key, account);
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> entry in snapshot.Storages)
            batch.SetStorage(entry.Key.Key.Item1, entry.Key.Key.Item2, entry.Value);
        foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> entry in snapshot.StateNodes)
            if (entry.Value.FullRlp.Length != 0 || entry.Value.NodeType != NodeType.Unknown)
                batch.SetStateTrieNode(entry.Key.Key, entry.Value.FullRlp.AsSpan());
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> entry in snapshot.StorageNodes)
            if (entry.Value.FullRlp.Length != 0 || entry.Value.NodeType != NodeType.Unknown)
                batch.SetStorageTrieNode(entry.Key.Key.Item1, entry.Key.Key.Item2, entry.Value.FullRlp.AsSpan());
    }

    private sealed class GenesisScopeProvider(MigrationGenesisBootstrap owner) : IWorldStateScopeProvider
    {
        private int _opened;

        public bool HasRoot(BlockHeader? baseBlock, BlockHeader? targetBlock) => baseBlock is null && Volatile.Read(ref _opened) == 0;

        public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, BlockHeader? targetBlock, LocalMetrics metrics)
        {
            if (baseBlock is not null || Interlocked.CompareExchange(ref _opened, 1, 0) != 0)
                throw new InvalidOperationException("The genesis source scope opens once, at the pre-genesis state.");
            return owner.Open();
        }
    }

    private sealed class EmptyTrieNodeCache : ITrieNodeCache
    {
        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            node = null;
            return false;
        }
        public void Add(TransientResource transientResource) { }
        public void Clear() { }
    }
}
