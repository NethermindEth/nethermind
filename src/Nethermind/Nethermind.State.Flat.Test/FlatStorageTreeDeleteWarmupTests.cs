// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// A block that deletes slots of a storage trie held in persistence: the slots are hinted as the block writes them,
/// the warmer runs, and the block-end commit should then find every trie node it needs already loaded.
/// </summary>
public class FlatStorageTreeDeleteWarmupTests
{
    private const int TrieSlots = 2_000;
    private const int DeletedSlots = 64;

    // The per-block node cache is direct-mapped, so a few warmed nodes are evicted by others whatever is written;
    // a delete must not add the nodes its collapse needs on top of that. Slots are hashed with a per-process seed,
    // so the collisions vary run to run: a flagged delete has read up to 5 more nodes, an unflagged one always 25 more.
    private const int CollisionAllowance = 12;

    private static readonly Address Contract = TestItem.AddressA;

    [Test]
    public void Deletes_hinted_by_the_block_cost_the_commit_no_more_reads_than_updates([Values] bool hintWithoutValueFirst)
    {
        using Fixture fixture = new();
        UInt256[] slots = fixture.PickSlots();

        long updateLoads = fixture.CommitLoads(slots, delete: false, passDeleteFlag: true, out _);
        // With hintWithoutValueFirst the prewarmer hints a slot before the block writes it, which marks the slot in
        // the warm-up dedupe: the delete hint must still be queued.
        long deleteLoads = fixture.CommitLoads(slots, delete: true, passDeleteFlag: true, out Hash256 storageRoot, hintWithoutValueFirst);

        Assert.That(deleteLoads, Is.LessThanOrEqualTo(updateLoads + CollisionAllowance), "the commit read collapse siblings the warm-up should have loaded");
        Assert.That(storageRoot, Is.EqualTo(fixture.ExpectedRootWithout(slots)));
    }

    [Test]
    public void Deletes_warmed_only_along_their_path_read_the_collapse_siblings_in_the_commit()
    {
        using Fixture fixture = new();
        UInt256[] slots = fixture.PickSlots();

        long updateLoads = fixture.CommitLoads(slots, delete: false, passDeleteFlag: false, out _);
        long deleteLoads = fixture.CommitLoads(slots, delete: true, passDeleteFlag: false, out Hash256 storageRoot);

        // Not a requirement: it shows these deletes do collapse branches, so the test above can fail.
        Assert.That(deleteLoads, Is.GreaterThan(updateLoads + CollisionAllowance));
        Assert.That(storageRoot, Is.EqualTo(fixture.ExpectedRootWithout(slots)));
    }

    private static UInt256 Slot(int index) => new(Keccak.Compute(BitConverter.GetBytes(index)).Bytes, isBigEndian: true);

    private static UInt256 ValueOf(int index) => (UInt256)(ulong)(index + 1);

    private sealed class Fixture : IDisposable
    {
        private readonly FlatDbConfig _config = new() { ApplyStorageWritesOnIdleThread = false };
        private readonly ResourcePool _resourcePool;
        private readonly RocksDbPersistence _persistence = new(new SnapshotableMemColumnsDb<FlatDbColumns>(), LimboLogs.Instance, NullTrieNodeLog.Instance);
        private readonly StateId _baseState;
        private readonly List<INodeRecordBuffer> _buffers = [];
        private readonly List<Snapshot> _snapshots = [];

        public Fixture()
        {
            _resourcePool = new ResourcePool(_config);
            SnapshotBundle bundle = new(
                new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
                NewBuffer(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
            CapturingCommitTarget target = new(_snapshots);
            using FlatWorldStateScope scope = new(StateId.PreGenesis, bundle, new NullCodeDb(), target, _config, new NoopTrieWarmer(), LimboLogs.Instance);

            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
            {
                batch.Set(Contract, new Account(1));
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(Contract, TrieSlots);
                for (int i = 0; i < TrieSlots; i++) storageBatch.Set(Slot(i), ValueOf(i));
            }

            scope.Commit(0);
            _baseState = new StateId(0, scope.RootHash);
            Persist(target.Snapshot ?? throw new InvalidOperationException("The base commit produced no snapshot"));

            // A node cache grows to fit the blocks before it; a fresh one is far smaller than a running node's.
            for (int block = 0; block < 2; block++)
            {
                (FlatWorldStateScope warming, _) = OpenScope(passDeleteFlag: false);
                IWorldStateScopeProvider.IStorageTree tree = warming.CreateStorageTree(Contract);
                for (int i = 0; i < TrieSlots; i++) tree.HintSet(Slot(i));
                warming.Dispose();
            }
        }

        /// <summary>Hints <paramref name="slots"/> as a block writing them would, then counts the persistence reads of the block-end commit.</summary>
        public long CommitLoads(UInt256[] slots, bool delete, bool passDeleteFlag, out Hash256 storageRoot, bool hintWithoutValueFirst = false)
        {
            (FlatWorldStateScope scope, CountingReader reader) = OpenScope(passDeleteFlag);
            try
            {
                UInt256 value = delete ? UInt256.Zero : (UInt256)123_456_789;
                IWorldStateScopeProvider.IStorageTree storageTree = scope.CreateStorageTree(Contract);
                if (hintWithoutValueFirst)
                {
                    foreach (UInt256 slot in slots) storageTree.HintSet(slot);
                }

                foreach (UInt256 slot in slots) storageTree.HintSet(slot, value);

                long before = reader.Loads;
                using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
                {
                    using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(Contract, slots.Length);
                    foreach (UInt256 slot in slots) storageBatch.Set(slot, value);
                }

                scope.Commit(1);
                storageRoot = scope.Get(Contract)!.StorageRoot;
                return reader.Loads - before;
            }
            finally
            {
                scope.Dispose();
            }
        }

        public (FlatWorldStateScope Scope, CountingReader Reader) OpenScope(bool passDeleteFlag)
        {
            CountingReader reader = new(_persistence.CreateReader());
            SnapshotBundle bundle = new(
                new ReadOnlySnapshotBundle(new SnapshotPooledList(0), reader, recordDetailedMetrics: false, PersistedSnapshotStack.Empty()),
                NewBuffer(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
            FlatWorldStateScope scope = new(_baseState, bundle, new NullCodeDb(), new CapturingCommitTarget(_snapshots), _config, new InlineTrieWarmer(passDeleteFlag), LimboLogs.Instance);
            return (scope, reader);
        }

        public UInt256[] PickSlots()
        {
            Random random = new(7);
            return Enumerable.Range(0, TrieSlots).OrderBy(_ => random.Next()).Take(DeletedSlots).Select(Slot).ToArray();
        }

        public Hash256 ExpectedRootWithout(UInt256[] deleted)
        {
            HashSet<UInt256> gone = [.. deleted];
            StorageTree expected = new(new RawScopedTrieStore(new TestMemDb()), LimboLogs.Instance);
            for (int i = 0; i < TrieSlots; i++)
            {
                UInt256 slot = Slot(i);
                if (!gone.Contains(slot)) expected.Set(slot, ValueOf(i).ToMinimalBigEndian());
            }

            expected.UpdateRootHash();
            return expected.RootHash;
        }

        private void Persist(Snapshot snapshot)
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To, WriteFlags.None);
            foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts) batch.SetAccount(kv.Key.Key, kv.Value);
            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?> kv in snapshot.Storages) batch.SetStorage(kv.Key.Key.Item1, kv.Key.Key.Item2, kv.Value);
            foreach ((TreePath path, ulong pointer) in snapshot.StateNodes) batch.SetStateTrieNode(path, snapshot.GetRlp(pointer));
            foreach ((Hash256 address, TreePath path, ulong pointer) in snapshot.StorageNodes) batch.SetStorageTrieNode(address, path, snapshot.GetRlp(pointer));
        }

        private INodeRecordBuffer NewBuffer()
        {
            INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
            _buffers.Add(buffer);
            return buffer;
        }

        public void Dispose()
        {
            _persistence.Clear();
            foreach (Snapshot snapshot in _snapshots) snapshot.Dispose();
            foreach (INodeRecordBuffer buffer in _buffers) buffer.Dispose();
        }
    }

    /// <summary>Runs each job inside its push. Without <c>passDeleteFlag</c> it warms only the path, as before deletes were flagged.</summary>
    private sealed class InlineTrieWarmer(bool passDeleteFlag) : ITrieWarmer
    {
        public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
            => PushSlotJobMpmc(storageTree, index, sequenceId, isDelete);

        public bool PushSlotJobMpmc(ITrieWarmer.IStorageWarmer storageTree, in UInt256 index, int sequenceId, bool isDelete)
        {
            storageTree.WarmUpStorageTrie(index, sequenceId, isDelete && passDeleteFlag);
            return true;
        }

        public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
        {
            if (path is not null) scope.WarmUpStateTrie(path, sequenceId);
            return true;
        }

        public void OnEnterScope() { }

        public void OnExitScope() { }
    }

    private sealed class CountingReader(IPersistence.IPersistenceReader inner) : IPersistence.IPersistenceReader
    {
        private long _loads;

        public long Loads => Interlocked.Read(ref _loads);

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags)
        {
            Interlocked.Increment(ref _loads);
            return inner.TryLoadStateRlp(path, flags);
        }

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags)
        {
            Interlocked.Increment(ref _loads);
            return inner.TryLoadStorageRlp(address, path, flags);
        }

        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            Interlocked.Increment(ref _loads);
            return inner.TryLoadStateRlp(path, flags, action, arg);
        }

        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            Interlocked.Increment(ref _loads);
            return inner.TryLoadStorageRlp(address, path, flags, action, arg);
        }

        public Account? GetAccount(Address address) => inner.GetAccount(address);
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => inner.TryGetSlot(address, slot, ref outValue);
        public StateId CurrentState => inner.CurrentState;
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Keeps the snapshot and hands the block's node cache back to its pool, as the database manager does once done with it.</summary>
    private sealed class CapturingCommitTarget(List<Snapshot> snapshots) : IFlatCommitTarget
    {
        public Snapshot? Snapshot { get; private set; }

        public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
        {
            Snapshot = snapshot;
            snapshots.Add(snapshot);
            transientResource.ReleaseLease();
        }
    }

    private sealed class NullCodeDb : IWorldStateScopeProvider.ICodeDb
    {
        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) => default;

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new NullCodeSetter();

        private sealed class NullCodeSetter : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) { }

            public void Dispose() { }
        }
    }
}
