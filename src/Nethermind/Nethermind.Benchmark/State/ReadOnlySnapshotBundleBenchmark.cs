// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;

namespace Nethermind.Benchmarks.State;

[MemoryDiagnoser]
[WarmupCount(3)]
[MinIterationCount(3)]
[MaxIterationCount(10)]
public class ReadOnlySnapshotBundleBenchmark
{
    private ReadOnlySnapshotBundle _bundle = null!;

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

    private const int SnapshotCount = 8;
    private const int ArraySize = 32;

    [GlobalSetup]
    public void Setup()
    {
        FlatDbConfig config = new FlatDbConfig();
        ResourcePool resourcePool = new ResourcePool(config);
        List<FlatSnapshot> allSnapshots = new List<FlatSnapshot>(SnapshotCount);
        StateId currentStateId = new StateId(0, Keccak.EmptyTreeHash);

        int totalAccountCount = 0;
        int totalStorageAccountCount = 0;
        int maxSlotsPerStorageAccount = 0;

        // Track storage account ranges per snapshot for hit distribution
        List<(int AddressStart, int StorageCount, int SlotsPerAccount)> storageRanges = new();

        for (int block = 0; block < SnapshotCount; block++)
        {
            int multiplier = block < 6 ? 16 : 1;
            int accountCount = 1000 * multiplier;
            int storageAccountCount = 20 * multiplier;
            int slotsPerStorageAccount = 100 * multiplier;

            // Build ReadOnlySnapshotBundle from previously captured snapshots
            SnapshotPooledList prevSnapshots = new SnapshotPooledList(allSnapshots.Count);
            foreach (FlatSnapshot s in allSnapshots)
            {
                s.TryAcquire();
                prevSnapshots.Add(s);
            }

            ReadOnlySnapshotBundle readOnly = new ReadOnlySnapshotBundle(
                prevSnapshots, new NoopPersistenceReader(), recordDetailedMetrics: false);
            NullTrieNodeCache cache = new NullTrieNodeCache();
            SnapshotBundle bundle = new SnapshotBundle(
                readOnly, cache, resourcePool, ResourcePool.Usage.MainBlockProcessing);
            CapturingCommitTarget commitTarget = new CapturingCommitTarget();
            FlatWorldStateScope scope = new FlatWorldStateScope(
                currentStateId: currentStateId,
                snapshotBundle: bundle,
                codeDb: new NullCodeDb(),
                commitTarget: commitTarget,
                configuration: config,
                trieCacheWarmer: new NoopTrieWarmer(),
                logManager: NullLogManager.Instance);

            int addressOffset = totalAccountCount;

            // Pre-compute addresses in parallel (DeriveAddress involves Keccak)
            Address[] addresses = new Address[accountCount];
            int offset = addressOffset;
            Parallel.For(0, accountCount, i =>
            {
                addresses[i] = DeriveAddress(offset + i + 1);
            });

            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch =
                scope.StartWriteBatch(accountCount))
            {
                // Phase 1 (sequential): set accounts and create storage write batches
                IWorldStateScopeProvider.IStorageWriteBatch[] storageBatches =
                    new IWorldStateScopeProvider.IStorageWriteBatch[storageAccountCount];
                for (int i = 0; i < accountCount; i++)
                {
                    batch.Set(addresses[i], new Account(balance: (UInt256)(addressOffset + i + 1)));

                    if (i < storageAccountCount)
                    {
                        storageBatches[i] = batch.CreateStorageWriteBatch(addresses[i],
                            estimatedEntries: slotsPerStorageAccount);
                    }
                }

                // Phase 2 (parallel): fill storage slots — each FlatStorageTree is independent
                int slots = slotsPerStorageAccount;
                Parallel.For(0, storageAccountCount, i =>
                {
                    IWorldStateScopeProvider.IStorageWriteBatch storageBatch = storageBatches[i];
                    for (int s = 0; s < slots; s++)
                    {
                        storageBatch.Set((UInt256)(ulong)(s + 1),
                            new byte[] { (byte)((s + 1) & 0xFF) });
                    }

                    storageBatch.Dispose();
                });
            }

            scope.Commit(blockNumber: block + 1);

            FlatSnapshot snapshot = commitTarget.LastSnapshot
                ?? throw new InvalidOperationException(
                    $"Block {block + 1}: Commit produced no snapshot");
            snapshot.TryAcquire();
            allSnapshots.Add(snapshot);

            currentStateId = new StateId(block + 1, scope.RootHash);
            storageRanges.Add((totalAccountCount + 1, storageAccountCount, slotsPerStorageAccount));
            totalAccountCount += accountCount;
            totalStorageAccountCount += storageAccountCount;
            if (slotsPerStorageAccount > maxSlotsPerStorageAccount)
                maxSlotsPerStorageAccount = slotsPerStorageAccount;
        }

        // Build final ReadOnlySnapshotBundle with all 8 snapshots
        SnapshotPooledList finalSnapshots = new SnapshotPooledList(allSnapshots.Count);
        foreach (FlatSnapshot s in allSnapshots)
        {
            s.TryAcquire();
            finalSnapshots.Add(s);
        }

        _bundle = new ReadOnlySnapshotBundle(
            finalSnapshots, new NoopPersistenceReader(), recordDetailedMetrics: false);

        // --- Hit arrays ---
        _hitAccounts = new Address[ArraySize];
        int accountStep = Math.Max(1, totalAccountCount / ArraySize);
        for (int i = 0; i < ArraySize; i++)
        {
            int accountIndex = (i * accountStep % totalAccountCount) + 1;
            _hitAccounts[i] = DeriveAddress(accountIndex);
        }

        // Hit slots: spread across all snapshots so lookups hit different depth positions
        _hitSlots = new (Address, UInt256)[ArraySize];
        for (int i = 0; i < ArraySize; i++)
        {
            var range = storageRanges[i % storageRanges.Count];
            int storageAccountIndex = range.AddressStart + (i / storageRanges.Count % range.StorageCount);
            UInt256 slot = (UInt256)(ulong)((i * 97 % range.SlotsPerAccount) + 1);
            _hitSlots[i] = (DeriveAddress(storageAccountIndex), slot);
        }

        // Collect state/storage trie nodes from all snapshots
        List<TreePath> shortPaths = new List<TreePath>(ArraySize);
        List<TreePath> longPaths = new List<TreePath>(ArraySize);
        List<(Hash256, TreePath)> storageNodesList = new List<(Hash256, TreePath)>(ArraySize);

        foreach (FlatSnapshot snapshot in allSnapshots)
        {
            if (shortPaths.Count < ArraySize || longPaths.Count < ArraySize)
            {
                foreach (KeyValuePair<HashedKey<TreePath>, TrieNode> kv in snapshot.StateNodes)
                {
                    if (shortPaths.Count < ArraySize && kv.Key.Key.Length <= 15)
                        shortPaths.Add(kv.Key.Key);
                    if (longPaths.Count < ArraySize && kv.Key.Key.Length > 15)
                        longPaths.Add(kv.Key.Key);
                    if (shortPaths.Count >= ArraySize && longPaths.Count >= ArraySize)
                        break;
                }
            }

            if (storageNodesList.Count < ArraySize)
            {
                foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes)
                {
                    storageNodesList.Add((kv.Key.Key.Item1, kv.Key.Key.Item2));
                    if (storageNodesList.Count >= ArraySize)
                        break;
                }
            }
        }

        _hitShortPaths = shortPaths.ToArray();
        _hitLongPaths = longPaths.Count > 0 ? longPaths.ToArray() : shortPaths.ToArray();
        _hitStorageNodes = storageNodesList.ToArray();

        // --- Same-account arrays (hot-contract pattern) ---
        Address sameAddr = DeriveAddress(1);
        _sameAccountSlots = new (Address, UInt256)[ArraySize];
        for (int i = 0; i < ArraySize; i++)
            _sameAccountSlots[i] = (sameAddr, (UInt256)(ulong)(i + 1));

        Hash256 sameAddrHash = Keccak.Compute(sameAddr.Bytes);
        List<(Hash256, TreePath)> sameAccountNodesList = new List<(Hash256, TreePath)>(ArraySize);
        foreach (FlatSnapshot snapshot in allSnapshots)
        {
            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kv in snapshot.StorageNodes)
            {
                if (kv.Key.Key.Item1 == sameAddrHash)
                {
                    sameAccountNodesList.Add((kv.Key.Key.Item1, kv.Key.Key.Item2));
                    if (sameAccountNodesList.Count >= ArraySize)
                        break;
                }
            }

            if (sameAccountNodesList.Count >= ArraySize) break;
        }

        _sameAccountStorageNodes = sameAccountNodesList.ToArray();

        // --- Miss arrays ---
        _missAccounts = new Address[ArraySize];
        for (int i = 0; i < ArraySize; i++)
            _missAccounts[i] = DeriveAddress(totalAccountCount + 200_001 + i);

        _missSlots = new (Address, UInt256)[ArraySize];
        for (int i = 0; i < ArraySize; i++)
        {
            Address storageAddr = DeriveAddress((i % 20) + 1);
            UInt256 missSlot = (UInt256)(ulong)(maxSlotsPerStorageAccount + 100 + i);
            _missSlots[i] = (storageAddr, missSlot);
        }

        _missShortPaths = new TreePath[ArraySize];
        _missLongPaths = new TreePath[ArraySize];
        for (int i = 0; i < ArraySize; i++)
        {
            Address nonExistent = DeriveAddress(totalAccountCount + 300_001 + i);
            ValueHash256 addrHash = ValueKeccak.Compute(nonExistent.Bytes);
            TreePath shortPath = TreePath.FromPath(addrHash.Bytes);
            shortPath = shortPath.Truncate(15);
            _missShortPaths[i] = shortPath;
            _missLongPaths[i] = TreePath.FromPath(addrHash.Bytes);
        }

        _missStorageNodes = new (Hash256, TreePath)[ArraySize];
        for (int i = 0; i < ArraySize; i++)
        {
            Address nonStorageAddr = DeriveAddress(totalAccountCount + 400_001 + i);
            Hash256 addrHash = Keccak.Compute(nonStorageAddr.Bytes);
            _missStorageNodes[i] = (addrHash, TreePath.Empty);
        }

        _index = 0;

        // Verify hit arrays are populated
        if (_hitAccounts.Length == 0)
            throw new InvalidOperationException("Hit accounts array is empty");
        if (_hitSlots.Length == 0)
            throw new InvalidOperationException("Hit slots array is empty");
        if (_hitShortPaths.Length == 0)
            throw new InvalidOperationException("No short state trie paths found (Length <= 15)");
        if (_hitStorageNodes.Length == 0)
            throw new InvalidOperationException(
                "No storage trie nodes found — storage tree commit may have failed");
        if (_sameAccountStorageNodes.Length == 0)
            throw new InvalidOperationException(
                "No same-account storage trie nodes found for hot-contract pattern benchmark");

        // Verify miss keys are actually absent
        if (_bundle.GetAccount(_missAccounts[0]) is not null)
            throw new InvalidOperationException(
                "Miss account should not be found in snapshot bundle");
    }

    [Benchmark]
    public Account GetAccount()
        => _bundle.GetAccount(_hitAccounts[_index++ % _hitAccounts.Length]);

    [Benchmark]
    public byte[] GetSlot()
    {
        (Address addr, UInt256 slot) = _hitSlots[_index++ % _hitSlots.Length];
        return _bundle.GetSlot(addr, in slot, selfDestructStateIdx: -1);
    }

    [Benchmark]
    public bool TryFindStateNodes_Short()
    {
        TreePath path = _hitShortPaths[_index++ % _hitShortPaths.Length];
        return _bundle.TryFindStateNodes(in path, Keccak.Zero, out _);
    }

    [Benchmark]
    public bool TryFindStateNodes_Long()
    {
        TreePath path = _hitLongPaths[_index++ % _hitLongPaths.Length];
        return _bundle.TryFindStateNodes(in path, Keccak.Zero, out _);
    }

    [Benchmark]
    public bool TryFindStorageNodes()
    {
        (Hash256 addrHash, TreePath path) = _hitStorageNodes[_index++ % _hitStorageNodes.Length];
        return _bundle.TryFindStorageNodes(addrHash, in path, Keccak.Zero, out _);
    }

    [Benchmark]
    public byte[] GetSlot_SameAccount()
    {
        (Address addr, UInt256 slot) = _sameAccountSlots[_index++ % _sameAccountSlots.Length];
        return _bundle.GetSlot(addr, in slot, selfDestructStateIdx: -1);
    }

    [Benchmark]
    public bool TryFindStorageNodes_SameAccount()
    {
        (Hash256 addrHash, TreePath path) =
            _sameAccountStorageNodes[_index++ % _sameAccountStorageNodes.Length];
        return _bundle.TryFindStorageNodes(addrHash, in path, Keccak.Zero, out _);
    }

    [Benchmark]
    public Account GetAccount_Miss()
        => _bundle.GetAccount(_missAccounts[_index++ % _missAccounts.Length]);

    [Benchmark]
    public byte[] GetSlot_Miss()
    {
        (Address addr, UInt256 slot) = _missSlots[_index++ % _missSlots.Length];
        return _bundle.GetSlot(addr, in slot, selfDestructStateIdx: -1);
    }

    [Benchmark]
    public bool TryFindStateNodes_Short_Miss()
    {
        TreePath path = _missShortPaths[_index++ % _missShortPaths.Length];
        return _bundle.TryFindStateNodes(in path, Keccak.Zero, out _);
    }

    [Benchmark]
    public bool TryFindStateNodes_Long_Miss()
    {
        TreePath path = _missLongPaths[_index++ % _missLongPaths.Length];
        return _bundle.TryFindStateNodes(in path, Keccak.Zero, out _);
    }

    [Benchmark]
    public bool TryFindStorageNodes_Miss()
    {
        (Hash256 addrHash, TreePath path) =
            _missStorageNodes[_index++ % _missStorageNodes.Length];
        return _bundle.TryFindStorageNodes(addrHash, in path, Keccak.Zero, out _);
    }

    private static Address DeriveAddress(int index) =>
        new Address(Keccak.Compute(Address.FromNumber((UInt256)(ulong)index).Bytes));

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

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite()
            => NullCodeSetter.Instance;

        private sealed class NullCodeSetter : IWorldStateScopeProvider.ICodeSetter
        {
            public static readonly NullCodeSetter Instance = new NullCodeSetter();

            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) { }

            public void Dispose() { }
        }
    }
}
