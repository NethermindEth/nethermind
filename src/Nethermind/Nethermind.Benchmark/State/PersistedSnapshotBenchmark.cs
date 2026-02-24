// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;

namespace Nethermind.Benchmarks.State;

[MemoryDiagnoser]
public class PersistedSnapshotBenchmark
{
    private PersistedSnapshot _persistedSnapshot = null!;
    private MemoryArenaManager _arenaManager = null!;
    private FlatSnapshot _snapshotForBuild = null!;

    // Hit arrays — sampled from actually written data
    private Address[] _hitAccounts = null!;
    private (Address Address, UInt256 Slot)[] _hitSlots = null!;
    private TreePath[] _hitShortPaths = null!;
    private TreePath[] _hitLongPaths = null!;
    private (Hash256 AddressHash, TreePath Path)[] _hitStorageNodes = null!;

    // Same-account arrays — all slots/nodes from one address (hot-contract pattern)
    private (Address Address, UInt256 Slot)[] _sameAccountSlots = null!;
    private (Hash256 AddressHash, TreePath Path)[] _sameAccountStorageNodes = null!;

    // Miss arrays — keys guaranteed absent from the snapshot
    private Address[] _missAccounts = null!;
    private (Address Address, UInt256 Slot)[] _missSlots = null!;
    private TreePath[] _missShortPaths = null!;
    private TreePath[] _missLongPaths = null!;
    private (Hash256 AddressHash, TreePath Path)[] _missStorageNodes = null!;

    private int _index;

    [Params(1, 8)]
    public int Scale { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        FlatDbConfig config = new FlatDbConfig();
        ResourcePool resourcePool = new ResourcePool(config);
        SnapshotPooledList emptySnapshots = new SnapshotPooledList(0);
        NoopPersistenceReader reader = new NoopPersistenceReader();
        PersistedSnapshotList emptyPersisted = new PersistedSnapshotList(initial: 0);
        ReadOnlySnapshotBundle readOnly = new ReadOnlySnapshotBundle(
            emptySnapshots, reader, recordDetailedMetrics: false, emptyPersisted);
        NullTrieNodeCache cache = new NullTrieNodeCache();
        SnapshotBundle bundle = new SnapshotBundle(
            readOnly, cache, resourcePool, ResourcePool.Usage.MainBlockProcessing);
        CapturingCommitTarget commitTarget = new CapturingCommitTarget();
        StateId initialStateId = new StateId(0, Keccak.EmptyTreeHash);
        FlatWorldStateScope scope = new FlatWorldStateScope(
            currentStateId: initialStateId,
            snapshotBundle: bundle,
            codeDb: new NullCodeDb(),
            commitTarget: commitTarget,
            configuration: config,
            trieCacheWarmer: new NoopTrieWarmer(),
            logManager: NullLogManager.Instance);

        int AccountCount = 2000 * Scale;
        int StorageAccountCount = 20 * Scale;
        int SlotsPerStorageAccount = 100 * Scale;

        // Populate accounts. Only the first StorageAccountCount accounts have storage.
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(AccountCount))
        {
            for (int i = 0; i < AccountCount; i++)
            {
                Address addr = Address.FromNumber((UInt256)(ulong)(i + 1));
                batch.Set(addr, new Account(balance: (UInt256)(i + 1)));

                if (i < StorageAccountCount)
                {
                    using IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                        batch.CreateStorageWriteBatch(addr, estimatedEntries: SlotsPerStorageAccount);
                    for (int s = 0; s < SlotsPerStorageAccount; s++)
                    {
                        storageBatch.Set((UInt256)(ulong)(s + 1), new byte[] { (byte)((s + 1) & 0xFF) });
                    }
                }
            }
        }

        scope.Commit(blockNumber: 1);

        FlatSnapshot snapshot = commitTarget.LastSnapshot
            ?? throw new InvalidOperationException("GlobalSetup: Commit produced no snapshot");
        _snapshotForBuild = snapshot;

        const int ArraySize = 32;

        // --- Hit arrays ---
        _hitAccounts = new Address[ArraySize];
        int step = Math.Max(1, AccountCount / ArraySize);
        for (int i = 0; i < ArraySize; i++)
        {
            int accountIndex = (i * step % AccountCount) + 1;
            _hitAccounts[i] = Address.FromNumber((UInt256)(ulong)accountIndex);
        }

        _hitSlots = new (Address, UInt256)[ArraySize];
        int storageStep = Math.Max(1, StorageAccountCount / ArraySize);
        for (int i = 0; i < ArraySize; i++)
        {
            int storageAccountIndex = (i * storageStep % StorageAccountCount) + 1;
            Address storageAddr = Address.FromNumber((UInt256)(ulong)storageAccountIndex);
            UInt256 slot = (UInt256)(ulong)((i % SlotsPerStorageAccount) + 1);
            _hitSlots[i] = (storageAddr, slot);
        }

        List<TreePath> shortPaths = new List<TreePath>(ArraySize);
        List<TreePath> longPaths = new List<TreePath>(ArraySize);
        foreach (KeyValuePair<TreePath, TrieNode> kv in snapshot.StateNodes)
        {
            if (shortPaths.Count < ArraySize && kv.Key.Length <= 15)
                shortPaths.Add(kv.Key);
            if (longPaths.Count < ArraySize && kv.Key.Length > 15)
                longPaths.Add(kv.Key);
            if (shortPaths.Count >= ArraySize && longPaths.Count >= ArraySize)
                break;
        }
        _hitShortPaths = shortPaths.ToArray();
        // Fall back to short paths if the trie depth produces no paths > 15 nibbles
        _hitLongPaths = longPaths.Count > 0 ? longPaths.ToArray() : shortPaths.ToArray();

        List<(Hash256, TreePath)> storageNodes = new List<(Hash256, TreePath)>(ArraySize);
        foreach (KeyValuePair<(Hash256AsKey, TreePath), TrieNode> kv in snapshot.StorageNodes)
        {
            storageNodes.Add((kv.Key.Item1.Value, kv.Key.Item2));
            if (storageNodes.Count >= ArraySize)
                break;
        }
        _hitStorageNodes = storageNodes.ToArray();

        // --- Same-account arrays (hot-contract pattern) ---
        Address sameAddr = Address.FromNumber((UInt256)1UL);
        _sameAccountSlots = new (Address, UInt256)[ArraySize];
        for (int i = 0; i < ArraySize; i++)
        {
            _sameAccountSlots[i] = (sameAddr, (UInt256)(ulong)(i + 1));
        }

        Hash256 sameAddrHash = Keccak.Compute(sameAddr.Bytes);
        List<(Hash256, TreePath)> sameAccountNodes = new List<(Hash256, TreePath)>(ArraySize);
        foreach (KeyValuePair<(Hash256AsKey, TreePath), TrieNode> kv in snapshot.StorageNodes)
        {
            if (kv.Key.Item1.Value == sameAddrHash)
            {
                sameAccountNodes.Add((kv.Key.Item1.Value, kv.Key.Item2));
                if (sameAccountNodes.Count >= ArraySize)
                    break;
            }
        }
        _sameAccountStorageNodes = sameAccountNodes.ToArray();

        // --- Miss arrays ---
        _missAccounts = new Address[ArraySize];
        for (int i = 0; i < ArraySize; i++)
        {
            // Beyond written range
            _missAccounts[i] = Address.FromNumber((UInt256)(ulong)(AccountCount + 200_001 + i));
        }

        _missSlots = new (Address, UInt256)[ArraySize];
        for (int i = 0; i < ArraySize; i++)
        {
            // Storage account address paired with slot beyond written range
            Address storageAddr = Address.FromNumber((UInt256)(ulong)((i % StorageAccountCount) + 1));
            UInt256 missSlot = (UInt256)(ulong)(SlotsPerStorageAccount + 100 + i);
            _missSlots[i] = (storageAddr, missSlot);
        }

        _missShortPaths = new TreePath[ArraySize];
        _missLongPaths = new TreePath[ArraySize];
        for (int i = 0; i < ArraySize; i++)
        {
            Address nonExistent = Address.FromNumber((UInt256)(ulong)(AccountCount + 300_001 + i));
            ValueHash256 addrHash = ValueKeccak.Compute(nonExistent.Bytes);
            // Short: truncate to 15 nibbles
            TreePath shortPath = TreePath.FromPath(addrHash.Bytes);
            shortPath = shortPath.Truncate(15);
            _missShortPaths[i] = shortPath;
            // Long: full 64-nibble path
            _missLongPaths[i] = TreePath.FromPath(addrHash.Bytes);
        }

        _missStorageNodes = new (Hash256, TreePath)[ArraySize];
        for (int i = 0; i < ArraySize; i++)
        {
            // Use address hashes of non-storage accounts as the address hash key
            Address nonStorageAddr = Address.FromNumber((UInt256)(ulong)(StorageAccountCount + i + 1));
            Hash256 addrHash = Keccak.Compute(nonStorageAddr.Bytes);
            _missStorageNodes[i] = (addrHash, TreePath.Empty);
        }

        _index = 0;

        _arenaManager = new MemoryArenaManager(arenaSize: 256 * 1024 * 1024);
        byte[] data = PersistedSnapshotBuilder.Build(snapshot);
        SnapshotLocation loc = _arenaManager.Allocate(data);
        ArenaReservation reservation = _arenaManager.Open(loc);
        _persistedSnapshot = new PersistedSnapshot(
            id: 0,
            from: initialStateId,
            to: new StateId(1, scope.RootHash),
            type: PersistedSnapshotType.Base,
            reservation: reservation);

        // Verify hit arrays are populated (thrown in Release too, unlike Debug.Assert)
        if (_hitAccounts.Length == 0) throw new InvalidOperationException("Hit accounts array is empty");
        if (_hitSlots.Length == 0) throw new InvalidOperationException("Hit slots array is empty");
        if (_hitShortPaths.Length == 0)
            throw new InvalidOperationException("No short state trie paths found (Length <= 15)");
        if (_hitStorageNodes.Length == 0)
            throw new InvalidOperationException("No storage trie nodes found — storage tree commit may have failed");

        // Verify miss keys are actually absent
        if (_persistedSnapshot.TryGetAccount(_missAccounts[0], out _))
            throw new InvalidOperationException("Miss account should not be found in persisted snapshot");
    }

    [Benchmark]
    public byte[] Build() => PersistedSnapshotBuilder.Build(_snapshotForBuild);

    [Benchmark]
    public bool TryGetAccount() =>
        _persistedSnapshot.TryGetAccount(_hitAccounts[_index++ % _hitAccounts.Length], out _);

    [Benchmark]
    public bool TryGetSlot()
    {
        (Address addr, UInt256 slot) = _hitSlots[_index++ % _hitSlots.Length];
        return _persistedSnapshot.TryGetSlot(addr, in slot, out _);
    }

    [Benchmark]
    public bool TryLoadStateNodeRlp_Short()
    {
        TreePath path = _hitShortPaths[_index++ % _hitShortPaths.Length];
        return _persistedSnapshot.TryLoadStateNodeRlp(in path, out _);
    }

    [Benchmark]
    public bool TryLoadStateNodeRlp_Long()
    {
        TreePath path = _hitLongPaths[_index++ % _hitLongPaths.Length];
        return _persistedSnapshot.TryLoadStateNodeRlp(in path, out _);
    }

    [Benchmark]
    public bool TryLoadStorageNodeRlp()
    {
        (Hash256 addrHash, TreePath path) = _hitStorageNodes[_index++ % _hitStorageNodes.Length];
        return _persistedSnapshot.TryLoadStorageNodeRlp(addrHash, in path, out _);
    }

    [Benchmark]
    public bool TryGetSlot_SameAccount()
    {
        (Address addr, UInt256 slot) = _sameAccountSlots[_index++ % _sameAccountSlots.Length];
        return _persistedSnapshot.TryGetSlot(addr, in slot, out _);
    }

    [Benchmark]
    public bool TryLoadStorageNodeRlp_SameAccount()
    {
        (Hash256 addrHash, TreePath path) = _sameAccountStorageNodes[_index++ % _sameAccountStorageNodes.Length];
        return _persistedSnapshot.TryLoadStorageNodeRlp(addrHash, in path, out _);
    }

    [Benchmark]
    public bool TryGetAccount_Miss() =>
        _persistedSnapshot.TryGetAccount(_missAccounts[_index++ % _missAccounts.Length], out _);

    [Benchmark]
    public bool TryGetSlot_Miss()
    {
        (Address addr, UInt256 slot) = _missSlots[_index++ % _missSlots.Length];
        return _persistedSnapshot.TryGetSlot(addr, in slot, out _);
    }

    [Benchmark]
    public bool TryLoadStateNodeRlp_Short_Miss()
    {
        TreePath path = _missShortPaths[_index++ % _missShortPaths.Length];
        return _persistedSnapshot.TryLoadStateNodeRlp(in path, out _);
    }

    [Benchmark]
    public bool TryLoadStateNodeRlp_Long_Miss()
    {
        TreePath path = _missLongPaths[_index++ % _missLongPaths.Length];
        return _persistedSnapshot.TryLoadStateNodeRlp(in path, out _);
    }

    [Benchmark]
    public bool TryLoadStorageNodeRlp_Miss()
    {
        (Hash256 addrHash, TreePath path) = _missStorageNodes[_index++ % _missStorageNodes.Length];
        return _persistedSnapshot.TryLoadStorageNodeRlp(addrHash, in path, out _);
    }

    private sealed class NullTrieNodeCache : ITrieNodeCache
    {
        public bool TryGet(Hash256 address, in TreePath path, Hash256 hash, out TrieNode node)
        {
            node = null;
            return false;
        }

        public void Add(TransientResource transientResource) { }

        public void Clear() { }
    }

    private sealed class CapturingCommitTarget : IFlatCommitTarget
    {
        public FlatSnapshot LastSnapshot { get; private set; }
        public TransientResource LastResource { get; private set; }

        public void AddSnapshot(FlatSnapshot snapshot, TransientResource transientResource)
        {
            LastSnapshot = snapshot;
            LastResource = transientResource;
        }
    }

    private sealed class NullCodeDb : IWorldStateScopeProvider.ICodeDb
    {
        public byte[] GetCode(in ValueHash256 codeHash) => null;

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => NullCodeSetter.Instance;

        private sealed class NullCodeSetter : IWorldStateScopeProvider.ICodeSetter
        {
            public static readonly NullCodeSetter Instance = new NullCodeSetter();

            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) { }

            public void Dispose() { }
        }
    }
}
