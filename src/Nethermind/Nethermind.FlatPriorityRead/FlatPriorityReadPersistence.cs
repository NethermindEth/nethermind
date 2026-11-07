// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.FlatPriorityRead;

/// <summary>
/// <see cref="IPersistence"/> decorator that answers flat slot reads of the configured contracts from an
/// <see cref="IPrioritySlotStore"/>, so they never reach the inner persistence.
/// </summary>
/// <remarks>
/// <para>
/// The store mirrors the inner persistence for those contracts, absent slots included. The constructor loads it from
/// the inner persistence, blocking until done, and every write batch is mirrored into it. A reader pairs an inner
/// reader with a store snapshot of the same <see cref="StateId"/>.
/// </para>
/// <para>
/// With <see cref="IFlatPriorityReadConfig.CacheTrie"/> the store also mirrors the storage tries of those contracts, read
/// by walking each trie from its root, so it holds the nodes the tries reach but not the ones earlier versions of the
/// tries left behind.
/// </para>
/// <para>
/// Range deletes and <see cref="Clear"/> are mirrored by reading the doomed slots and trie nodes from the inner
/// persistence as it was before the batch, so the store only needs point writes. A batch buffers its mirrored changes and commits them
/// right after the inner commit under one lock, so the store commits in the inner persistence's order even when sync
/// writes several batches at once.
/// </para>
/// </remarks>
public sealed class FlatPriorityReadPersistence : IPersistence
{
    private const int LoadCommitInterval = 1 << 20;
    private const int LoadQueueCapacity = 1 << 16;
    private const int ParallelWalkDepth = 2;
    private const int BranchChildCount = 16;
    private static readonly TimeSpan ReaderMatchTimeout = TimeSpan.FromSeconds(30);

    private readonly IPersistence _inner;
    private readonly IPrioritySlotStore _store;
    private readonly bool _isPreimageMode;
    private readonly ValueHash256[] _addressKeys;
    private readonly Hash256[] _accountPaths;
    private readonly bool _cacheTrie;
    private readonly FrozenDictionary<Address, int> _contractByAddress;
    private readonly FrozenDictionary<ValueHash256, int> _contractByAddressKey;
    private readonly FrozenDictionary<ValueHash256, int> _contractByCachedTrie;
    private readonly Lock _commitLock = new();

    public FlatPriorityReadPersistence(IPersistence inner, IPrioritySlotStore store, IFlatPriorityReadConfig config, ILogManager logManager)
    {
        _inner = inner;
        _store = store;
        (Address Address, string Name)[] contracts = ParseContracts(config.Addresses);

        using IPersistence.IPersistenceReader reader = inner.CreateReader();
        _isPreimageMode = reader.IsPreimageMode;
        _cacheTrie = config.CacheTrie;
        _addressKeys = new ValueHash256[contracts.Length];
        _accountPaths = new Hash256[contracts.Length];
        Dictionary<Address, int> contractByAddress = [];
        Dictionary<ValueHash256, int> contractByAddressKey = [];
        Dictionary<ValueHash256, int> contractByCachedTrie = [];
        for (int contract = 0; contract < contracts.Length; contract++)
        {
            _addressKeys[contract] = AddressKey(contracts[contract].Address);
            _accountPaths[contract] = contracts[contract].Address.ToAccountPath.ToCommitment();
            contractByAddress[contracts[contract].Address] = contract;
            contractByAddressKey[_addressKeys[contract]] = contract;
            if (_cacheTrie) contractByCachedTrie[_accountPaths[contract].ValueHash256] = contract;
        }

        _contractByAddress = contractByAddress.ToFrozenDictionary();
        _contractByAddressKey = contractByAddressKey.ToFrozenDictionary();
        _contractByCachedTrie = contractByCachedTrie.ToFrozenDictionary();

        Load(contracts, reader, logManager.GetClassLogger<FlatPriorityReadPersistence>());
    }

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        long started = Stopwatch.GetTimestamp();
        SpinWait spin = default;
        while (true)
        {
            IPersistence.IPersistenceReader inner = _inner.CreateReader(flags);
            IPrioritySlotStore.IReader store = _store.BeginRead();
            if (store.State == inner.CurrentState) return new PriorityReader(this, inner, store);

            // The two only differ between an inner commit and the store commit that follows it.
            StateId innerState = inner.CurrentState;
            StateId storeState = store.State;
            store.Dispose();
            inner.Dispose();
            if (Stopwatch.GetElapsedTime(started) > ReaderMatchTimeout)
            {
                throw new InvalidOperationException($"The priority slot store stayed at {storeState} while the flat persistence is at {innerState}");
            }

            spin.SpinOnce();
        }
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) =>
        new MirroringWriteBatch(this, _inner.CreateWriteBatch(from, to, flags), to);

    public void Flush() => _inner.Flush();

    public void Clear()
    {
        using Lock.Scope _ = _commitLock.EnterScope();
        List<SlotChange> changes = [];
        List<NodeChange> nodeChanges = [];
        using (IPersistence.IPersistenceReader reader = _inner.CreateReader())
        {
            for (int contract = 0; contract < _addressKeys.Length; contract++)
            {
                CollectRemovals(reader, contract, ValueKeccak.Zero, ValueKeccak.MaxValue, changes);
                if (_cacheTrie) CollectNodeRemovals(reader, contract, ValueKeccak.Zero, ValueKeccak.MaxValue, nodeChanges);
            }
        }

        _inner.Clear();
        using IPersistence.IPersistenceReader cleared = _inner.CreateReader();
        CommitToStore(changes, nodeChanges, cleared.CurrentState);
    }

    private static (Address Address, string Name)[] ParseContracts(Dictionary<string, string>? addresses)
    {
        const string setting = $"FlatPriorityRead.{nameof(IFlatPriorityReadConfig.Addresses)}";
        if (addresses is not { Count: > 0 })
        {
            throw new InvalidConfigurationException($"{setting} must list at least one address.", ExitCodes.GeneralError);
        }

        (Address Address, string Name)[] parsed = new (Address, string)[addresses.Count];
        HashSet<Address> seen = [];
        int contract = 0;
        foreach ((string entry, string name) in addresses)
        {
            // Two spellings of one address are distinct keys of the map, so repeats are caught here.
            if (!Address.TryParse(entry, out Address? address) || address is null || !seen.Add(address))
            {
                throw new InvalidConfigurationException($"{setting} has an invalid or repeated address: {entry}.", ExitCodes.GeneralError);
            }

            parsed[contract++] = (address, name);
        }

        return parsed;
    }

    private ValueHash256 AddressKey(Address address)
    {
        if (!_isPreimageMode) return address.ToAccountPath;

        ValueHash256 key = default;
        address.Bytes.CopyTo(key.BytesAsSpan);
        return key;
    }

    private ValueHash256 SlotKey(in UInt256 slot)
    {
        ValueHash256 key = default;
        if (_isPreimageMode) slot.ToBigEndian(key.BytesAsSpan);
        else StorageTree.ComputeKeyWithLookup(slot, ref key);
        return key;
    }

    private void Load((Address Address, string Name)[] contracts, IPersistence.IPersistenceReader reader, ILogger logger)
    {
        long started = Stopwatch.GetTimestamp();
        long slots, nodes;
        string loaded;
        using (Loader loader = new(_store, reader.CurrentState, started, contracts.Length, _cacheTrie, logger))
        {
            for (int contract = 0; contract < contracts.Length; contract++)
            {
                loader.StartContract(contract, contracts[contract].Name);
                LoadSlots(reader, _addressKeys[contract], loader);
                if (_cacheTrie) LoadTrieNodes(reader, _accountPaths[contract], loader);
                loader.EndContract(contracts[contract].Address);
            }

            loader.Commit();
            (slots, nodes, loaded) = (loader.Slots, loader.Nodes, loader.Describe(loader.Slots, loader.Nodes));
        }

        // Opened before any batch can exist: a store may treat its first reader as the end of the load.
        using IPrioritySlotStore.IReader loadedStore = _store.BeginRead();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        Metrics.FlatPriorityReadLoadedSlots = slots;
        Metrics.FlatPriorityReadLoadedTrieNodes = nodes;
        Metrics.FlatPriorityReadLoadMilliseconds = (long)elapsed.TotalMilliseconds;
        if (logger.IsInfo) logger.Info($"Loaded {loaded} of {_addressKeys.Length} priority contracts in {elapsed.TotalSeconds:N1}s at state {loadedStore.State}");
    }

    private static void LoadSlots(IPersistence.IPersistenceReader reader, in ValueHash256 addressKey, Loader loader)
    {
        using IPersistence.IFlatIterator slots = reader.CreateStorageIterator(addressKey, ValueKeccak.Zero, ValueKeccak.MaxValue);
        while (slots.MoveNext())
        {
            // The iterator exposes only the stored encoding, which this read decodes.
            ValueHash256 slotKey = slots.CurrentKey;
            UInt256 value = default;
            if (reader.TryGetStorageRaw(addressKey, slotKey, ref value)) loader.Set(slotKey, value);
        }
    }

    /// <remarks>
    /// The trie is read on several threads while this thread writes what they read, as a store writer is tied to the
    /// thread that began it.
    /// </remarks>
    private static void LoadTrieNodes(IPersistence.IPersistenceReader reader, Hash256 account, Loader loader)
    {
        using CancellationTokenSource stop = new();
        using BlockingCollection<(TreePath Path, byte[] Rlp)> nodes = new(LoadQueueCapacity);
        Task walk = Task.Run(() =>
        {
            try
            {
                VisitStorageNodes(reader, account, TreePath.Empty, ValueKeccak.Zero, ValueKeccak.MaxValue, parallel: true,
                    (path, rlp) => nodes.Add((path, rlp), stop.Token));
            }
            finally
            {
                nodes.CompleteAdding();
            }
        });

        try
        {
            foreach ((TreePath path, byte[] rlp) in nodes.GetConsumingEnumerable()) loader.SetNode(path, rlp);
        }
        finally
        {
            // A failed write stops the walk, and either way the walk is done with the reader before this returns.
            stop.Cancel();
            Task.WaitAny(walk);
        }

        walk.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Passes each node of the storage trie of <paramref name="account"/> at or below <paramref name="path"/> whose keys
    /// reach into <c>[<paramref name="from"/>, <paramref name="to"/>]</c> to <paramref name="visit"/>, parents first.
    /// </summary>
    /// <remarks>
    /// Walks the trie, so it finds only the nodes the trie reaches. With <paramref name="parallel"/> the subtrees below
    /// the first <see cref="ParallelWalkDepth"/> levels are walked on several threads at once.
    /// </remarks>
    private static void VisitStorageNodes(IPersistence.IPersistenceReader reader, Hash256 account, TreePath path, ValueHash256 from, ValueHash256 to, bool parallel, Action<TreePath, byte[]> visit)
    {
        if (path.ToUpperBoundPath().CompareTo(from) < 0 || path.ToLowerBoundPath().CompareTo(to) > 0) return;
        if (reader.TryLoadStorageRlp(account, path, ReadFlags.None) is not { } rlp) return;

        visit(path, rlp);

        TrieNode node = new(NodeType.Unknown, rlp);
        node.ResolveNode(NullTrieNodeResolver.Instance, path);
        if (node.IsExtension)
        {
            if (node.GetChildHashAsValueKeccak(0, out _)) VisitStorageNodes(reader, account, path.Append(node.Key), from, to, parallel, visit);
            return;
        }

        if (!node.IsBranch) return;

        // An inline child is part of this node's RLP, never stored on its own, and too small to reference a stored node.
        int storedChildren = 0;
        for (int child = 0; child < BranchChildCount; child++)
        {
            if (node.GetChildHashAsValueKeccak(child, out _)) storedChildren |= 1 << child;
        }

        if (parallel && path.Length < ParallelWalkDepth) Parallel.For(0, BranchChildCount, VisitChild);
        else for (int child = 0; child < BranchChildCount; child++) VisitChild(child);

        void VisitChild(int child)
        {
            if ((storedChildren & (1 << child)) != 0) VisitStorageNodes(reader, account, path.Append(child), from, to, parallel, visit);
        }
    }

    private void CollectRemovals(IPersistence.IPersistenceReader reader, int contract, in ValueHash256 from, in ValueHash256 to, List<SlotChange> changes)
    {
        using IPersistence.IFlatIterator slots = reader.CreateStorageIterator(_addressKeys[contract], from, to);
        while (slots.MoveNext()) changes.Add(new SlotChange(contract, slots.CurrentKey, null));
    }

    /// <summary>Adds the removal of every node of the contract's storage trie wholly within <c>[from, to]</c>, as the inner persistence's own range delete does.</summary>
    private void CollectNodeRemovals(IPersistence.IPersistenceReader reader, int contract, in ValueHash256 from, in ValueHash256 to, List<NodeChange> changes)
    {
        ValueHash256 lowest = from;
        ValueHash256 highest = to;
        VisitStorageNodes(reader, _accountPaths[contract], TreePath.Empty, from, to, parallel: false, (path, _) =>
        {
            if (path.ToLowerBoundPath().CompareTo(lowest) >= 0 && path.ToUpperBoundPath().CompareTo(highest) <= 0) changes.Add(new NodeChange(contract, path, null));
        });
    }

    private void CommitToStore(List<SlotChange>? changes, List<NodeChange>? nodeChanges, in StateId state)
    {
        using IPrioritySlotStore.IWriter writer = _store.BeginWrite();
        foreach (SlotChange change in changes ?? [])
        {
            if (change.Value is { } value) writer.Set(change.Contract, change.SlotKey, value);
            else writer.Remove(change.Contract, change.SlotKey);
        }

        foreach (NodeChange change in nodeChanges ?? [])
        {
            if (change.Rlp is { } rlp) writer.SetNode(change.Contract, change.Path, rlp);
            else writer.RemoveNode(change.Contract, change.Path);
        }

        writer.Commit(state);
    }

    private readonly record struct SlotChange(int Contract, ValueHash256 SlotKey, UInt256? Value);

    private readonly record struct NodeChange(int Contract, TreePath Path, byte[]? Rlp);

    /// <summary>Writes the startup load into the store, committing every <see cref="LoadCommitInterval"/> entries and logging the progress at each.</summary>
    /// <remarks>Nothing reads the store before the load ends, so committing parts of it bounds the writer's memory.</remarks>
    private sealed class Loader(IPrioritySlotStore store, StateId state, long started, int contractCount, bool cacheTrie, ILogger logger) : IDisposable
    {
        private IPrioritySlotStore.IWriter _writer = store.BeginWrite();
        private int _contract;
        private string _name = "";
        private long _contractSlots;
        private long _contractNodes;

        public long Slots { get; private set; }
        public long Nodes { get; private set; }

        public void StartContract(int contract, string name) => (_contract, _name, _contractSlots, _contractNodes) = (contract, name, 0, 0);

        public void Set(in ValueHash256 slotKey, in UInt256 value)
        {
            _writer.Set(_contract, slotKey, value);
            Slots++;
            _contractSlots++;
            Wrote();
        }

        public void SetNode(in TreePath path, ReadOnlySpan<byte> rlp)
        {
            _writer.SetNode(_contract, path, rlp);
            Nodes++;
            _contractNodes++;
            Wrote();
        }

        public void EndContract(Address address)
        {
            if (logger.IsInfo) logger.Info($"Loaded {Describe(_contractSlots, _contractNodes)} of priority contract {_name} {address} ({_contract + 1}/{contractCount}), {Stopwatch.GetElapsedTime(started).TotalSeconds:N1}s");
        }

        public void Commit() => _writer.Commit(state);

        public string Describe(long slots, long nodes) => cacheTrie ? $"{slots:N0} slots and {nodes:N0} trie nodes" : $"{slots:N0} slots";

        public void Dispose() => _writer.Dispose();

        private void Wrote()
        {
            if ((Slots + Nodes) % LoadCommitInterval != 0) return;

            _writer.Commit(state);
            _writer.Dispose();
            _writer = store.BeginWrite();
            if (logger.IsInfo) logger.Info($"Loading the priority store: {Describe(Slots, Nodes)} so far, {Describe(_contractSlots, _contractNodes)} of {_name} ({_contract + 1}/{contractCount}), {Stopwatch.GetElapsedTime(started).TotalSeconds:N1}s");
        }
    }

    private sealed class PriorityReader(FlatPriorityReadPersistence parent, IPersistence.IPersistenceReader inner, IPrioritySlotStore.IReader store)
        : IPersistence.IPersistenceReader, IPersistence.IPrefetchingReader
    {
        // Captured once per reader, as in CarryForwardCachingPersistence, to keep the flag off the hot path.
        private readonly bool _recordDetailedMetrics = Db.Metrics.DetailedMetricsEnabled;
        private int _disposed;

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            if (!parent._contractByAddress.TryGetValue(address, out int contract)) return inner.TryGetSlot(address, slot, ref outValue);

            bool found = store.TryGet(contract, parent.SlotKey(slot), out UInt256 value);
            if (found) outValue = value;
            if (_recordDetailedMetrics)
            {
                if (found) Metrics.IncrementFound();
                else Metrics.IncrementNotFound();
            }

            return found;
        }

        public Account? GetAccount(Address address) => inner.GetAccount(address);
        public StateId CurrentState => inner.CurrentState;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => inner.TryLoadStateRlp(path, flags, action, arg);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            if (!parent._contractByCachedTrie.TryGetValue(address.ValueHash256, out int contract)) return inner.TryLoadStorageRlp(address, path, flags);

            byte[]? rlp = store.GetNode(contract, path);
            CountNodeRead(rlp is not null);
            return rlp;
        }

        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            if (!parent._contractByCachedTrie.TryGetValue(address.ValueHash256, out int contract)) return inner.TryLoadStorageRlp(address, path, flags, action, arg);

            bool found = store.TryGetNode(contract, path, action, arg);
            CountNodeRead(found);
            return found;
        }
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;

        public bool SupportsPrefetch => inner is IPersistence.IPrefetchingReader { SupportsPrefetch: true };

        /// <summary>Plans on the slot samples the store does not answer.</summary>
        public IPersistence.PrefetchColumns PlanPrefetch(Span<Address> accountSamples, Span<StorageCell> slotSamples) =>
            inner is IPersistence.IPrefetchingReader prefetcher
                ? prefetcher.PlanPrefetch(accountSamples, DropPriority(slotSamples))
                : IPersistence.PrefetchColumns.None;

        /// <summary>Forwards only the slots the store does not answer.</summary>
        public void Prefetch(IPersistence.PrefetchColumns columns, Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken)
        {
            if (inner is IPersistence.IPrefetchingReader prefetcher) prefetcher.Prefetch(columns, accounts, DropPriority(slots), cancellationToken);
        }

        // Compacted in place: the span is the caller's scratch.
        private Span<StorageCell> DropPriority(Span<StorageCell> slots)
        {
            int kept = 0;
            foreach (StorageCell cell in slots)
            {
                if (!parent._contractByAddress.ContainsKey(cell.Address)) slots[kept++] = cell;
            }

            return slots[..kept];
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            store.Dispose();
            inner.Dispose();
        }

        private void CountNodeRead(bool found)
        {
            if (!_recordDetailedMetrics) return;

            if (found) Metrics.IncrementTrieNodeFound();
            else Metrics.IncrementTrieNodeNotFound();
        }
    }

    private sealed class MirroringWriteBatch(FlatPriorityReadPersistence parent, IPersistence.IWriteBatch inner, StateId to) : IPersistence.IWriteBatch
    {
        private List<SlotChange>? _changes;
        private List<NodeChange>? _nodeChanges;
        private IPersistence.IPersistenceReader? _preBatchReader;

        private List<SlotChange> Changes => _changes ??= [];
        private List<NodeChange> NodeChanges => _nodeChanges ??= [];

        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value)
        {
            if (parent._contractByAddress.TryGetValue(addr, out int contract)) Changes.Add(new SlotChange(contract, parent.SlotKey(slot), value));
            inner.SetStorage(addr, slot, value);
        }

        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
        {
            if (parent._contractByAddressKey.TryGetValue(addrHash, out int contract))
            {
                RlpReader rlp = new(rlpValue);
                Changes.Add(new SlotChange(contract, slotHash, new UInt256(rlp.DecodeByteArraySpan(), isBigEndian: true)));
            }

            inner.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
        }

        public void SelfDestruct(Address addr)
        {
            if (parent._contractByAddress.TryGetValue(addr, out int contract)) RemovePreBatchStorage(contract);
            inner.SelfDestruct(addr);
        }

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            if (parent._contractByAddressKey.TryGetValue(addressHash, out int contract)) RemovePreBatchSlots(contract, fromPath, toPath);
            inner.DeleteStorageRange(addressHash, fromPath, toPath);
        }

        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            // Storage is keyed by the first 20 bytes of the account path, so the inner persistence compares only those.
            ReadOnlySpan<byte> lowest = fromPath.Bytes[..Address.Size];
            ReadOnlySpan<byte> highest = toPath.Bytes[..Address.Size];
            for (int contract = 0; contract < parent._addressKeys.Length; contract++)
            {
                // Slots are keyed by the address key, which is the account path unless in preimage mode; trie nodes always by the account path.
                if (InRange(parent._addressKeys[contract].Bytes[..Address.Size], lowest, highest))
                {
                    RemovePreBatchSlots(contract, ValueKeccak.Zero, ValueKeccak.MaxValue);
                }

                if (parent._cacheTrie && InRange(parent._accountPaths[contract].Bytes[..Address.Size], lowest, highest))
                {
                    RemovePreBatchNodes(contract, ValueKeccak.Zero, ValueKeccak.MaxValue);
                }
            }

            inner.SelfDestructRange(fromPath, toPath);

            static bool InRange(ReadOnlySpan<byte> key, ReadOnlySpan<byte> lowest, ReadOnlySpan<byte> highest) =>
                key.SequenceCompareTo(lowest) >= 0 && key.SequenceCompareTo(highest) <= 0;
        }

        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp)
        {
            // An empty span deletes the node, as in the inner persistence.
            if (parent._contractByCachedTrie.TryGetValue(address.ValueHash256, out int contract)) NodeChanges.Add(new NodeChange(contract, path, rlp.IsEmpty ? null : rlp.ToArray()));
            inner.SetStorageTrieNode(address, path, rlp);
        }

        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to)
        {
            if (parent._contractByCachedTrie.TryGetValue(addressHash, out int contract)) RemovePreBatchNodes(contract, from, to);
            inner.DeleteStorageTrieNodeRange(addressHash, from, to);
        }

        public void SetAccount(Address addr, Account? account) => inner.SetAccount(addr, account);
        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStateTrieNode(path, rlp);
        public void SetAccountRaw(in ValueHash256 addrHash, Account account) => inner.SetAccountRaw(addrHash, account);
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteAccountRange(fromPath, toPath);
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) => inner.DeleteStateTrieNodeRange(from, to);

        public void Dispose()
        {
            _preBatchReader?.Dispose();
            using Lock.Scope _ = parent._commitLock.EnterScope();
            inner.Dispose();
            parent.CommitToStore(_changes, _nodeChanges, to);
        }

        private void RemovePreBatchStorage(int contract)
        {
            RemovePreBatchSlots(contract, ValueKeccak.Zero, ValueKeccak.MaxValue);
            if (parent._cacheTrie) RemovePreBatchNodes(contract, ValueKeccak.Zero, ValueKeccak.MaxValue);
        }

        // Wipes remove what the inner persistence held before this batch, as its own wipes do.
        private void RemovePreBatchSlots(int contract, in ValueHash256 from, in ValueHash256 to) =>
            parent.CollectRemovals(PreBatchReader, contract, from, to, Changes);

        private void RemovePreBatchNodes(int contract, in ValueHash256 from, in ValueHash256 to) =>
            parent.CollectNodeRemovals(PreBatchReader, contract, from, to, NodeChanges);

        private IPersistence.IPersistenceReader PreBatchReader => _preBatchReader ??= parent._inner.CreateReader();
    }
}
