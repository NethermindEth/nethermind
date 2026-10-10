// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Buffers;
using Nethermind.Int256;
using Nethermind.State.Flat.Io;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Snapshot;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotCompactorTests
{
    private readonly PbtResourcePool _pool = new(new PbtConfig());
    private static readonly PbtConfig Config = new() { CompactSize = 16 };

    [Test]
    public void Compact_PreservesNewestCanonicalLeafAndGroupAfterSourcesAreDisposed([Values] bool tombstone)
    {
        PbtNodePath groupKey = new([], 0);
        PbtStorageNodePath alternateGroupKey = new([], 0);
        PbtVariableTreeKey key = PbtTestLeaves.SlotKey(TestItem.AddressA, 64);
        TrackingMemoryProvider memoryProvider = new();
        PbtSnapshotContent older = new();
        PbtSnapshotContent newer = new();
        older.SetSlot(key, new UInt256(TestItem.KeccakA.Bytes, isBigEndian: true));
        newer.SetSlot(key, new UInt256(TestItem.KeccakB.Bytes, isBigEndian: true));
        byte[] expected;
        using (RefCountingMemory olderPayload = CreateStorageLeafGroup())
        using (RefCountingMemory newerPayload = CreateStorageLeafGroup())
        {
            older.SetNodeGroup(groupKey, olderPayload);
            newer.SetNodeGroup(alternateGroupKey, tombstone ? null : newerPayload);
            expected = newerPayload.GetSpan().ToArray();
        }
        using (PbtSnapshot compacted = Compact(older, newer))
        {
            bool found = compacted.Content.TryGetNodeGroup(groupKey, out RefCountingMemory? payload);
            using RefCountingMemory? payloadLease = payload;
            bool alternateFound = compacted.Content.TryGetNodeGroup(alternateGroupKey, out RefCountingMemory? alternatePayload);
            using RefCountingMemory? alternatePayloadLease = alternatePayload;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(compacted.Content.GetSlot(key), Is.EqualTo(new UInt256(TestItem.KeccakB.Bytes, isBigEndian: true)));
                Assert.That(compacted.Content.NodeGroupCount(), Is.EqualTo(1));
                Assert.That(found, Is.True);
                Assert.That(alternateFound, Is.True);
                Assert.That(alternatePayload, Is.SameAs(payload));
                Assert.That(payload?.Memory.ToArray(), Is.EqualTo(tombstone ? null : expected));
            }
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);

        RefCountingMemory CreateStorageLeafGroup()
        {
            PbtTraversalPath path = PbtTraversalPath.FromPath(stackalloc byte[PbtVariableTreeKey.MaxLength], groupKey);
            using PbtNodeGroupWriter<PbtNodePath> writer = PbtNodeGroupWriter<PbtNodePath>.Rent(groupKey.BitDepth, memoryProvider);
            writer.Write(path, PbtThreeLevelGroupGeometry.RootPosition, PbtTreeHarness.EncodeLeaf(key));
            return writer.Detach(default, ushort.MaxValue)!;
        }
    }

    [Test]
    public void Compact_preserves_clear_ordering_and_whole_typed_values([Values(7u, 1000u)] uint slot, [Values] bool clearLast)
    {
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        PbtVariableTreeKey key = PbtTestLeaves.SlotKey(TestItem.AddressA, slot);
        PbtVariableTreeKey otherSlot = PbtTestLeaves.SlotKey(TestItem.AddressA, slot + 1);
        PbtVariableTreeKey otherAddress = PbtTestLeaves.SlotKey(TestItem.AddressB, slot);
        UInt256 original = (UInt256)0x01;
        UInt256 replacement = (UInt256)0x02;
        CodeInfo code = new(Bytes.FromHexString("6001600055"));
        Account account = Build.An.Account.WithNonce(7).WithBalance(9).WithStorageRoot(TestItem.KeccakB).WithCode(code.Code.ToArray()).TestObject;
        PbtSnapshotContent older = new();
        older.Accounts[addressHash] = Build.An.Account.TestObject.ToPbtAccount();
        older.SetSlot(key, original);
        older.SetSlot(otherSlot, original);
        older.SetSlot(otherAddress, original);
        PbtSnapshotContent clearing = new();
        clearing.ClearStorage(addressHash);
        PbtSnapshotContent writing = new();
        writing.SetSlot(key, replacement);
        PackedSlotRun writtenRun = writing.GetRun(key);
        writing.Accounts[addressHash] = PbtAccount.From(account, code);
        writing.Codes[account.CodeHash.ValueHash256] = code;
        using (PbtSnapshot compacted = Compact(older, clearLast ? writing : clearing, clearLast ? clearing : writing))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(compacted.Content.Accounts[addressHash], Is.EqualTo(PbtAccount.From(account, code)));
            Assert.That(compacted.Content.Codes[account.CodeHash.ValueHash256], Is.SameAs(code));
            Assert.That(compacted.Content.SelfDestructedStorageAddresses.ContainsKey(addressHash), Is.True);
            Assert.That(compacted.Content.TryGetSlot(key, out _), Is.EqualTo(!clearLast));
            if (!clearLast) Assert.That(compacted.Content.GetSlot(key), Is.EqualTo(replacement));
            if (!clearLast) Assert.That(compacted.Content.GetRun(key), Is.Not.SameAs(writtenRun), "compaction copies runs, the source layer keeps its own");
            // The rewritten run is whole, so the older layer's neighbouring slot does not survive the rewrite.
            Assert.That(compacted.Content.GetSlot(otherSlot), Is.EqualTo(UInt256.Zero));
            Assert.That(compacted.Content.GetSlot(otherAddress), Is.EqualTo(original));
        }
    }

    [Test]
    public void Retained_merge_preserves_clear_order_and_whole_runs([Values(7u, 1000u)] uint slot,
        [Values] bool clearLast, [Values] bool writeInClearLayer)
    {
        using PbtRetainedTestStore store = new();
        ValueHash256 address = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        PbtVariableTreeKey key = PbtTestLeaves.SlotKey(TestItem.AddressA, slot);
        PbtVariableTreeKey neighbor = PbtTestLeaves.SlotKey(TestItem.AddressA, slot + 1);
        PbtVariableTreeKey differentRun = PbtTestLeaves.SlotKey(TestItem.AddressA, slot + 16);
        PbtVariableTreeKey otherAddress = PbtTestLeaves.SlotKey(TestItem.AddressB, slot);
        UInt256 original = (UInt256)1;
        UInt256 replacement = (UInt256)2;
        PbtSnapshotContent older = new();
        older.SetSlot(key, original);
        older.SetSlot(neighbor, original);
        older.SetSlot(differentRun, original);
        older.SetSlot(otherAddress, original);
        older.Accounts[address] = Build.An.Account.TestObject.ToPbtAccount();
        PbtSnapshotContent clearing = new();
        clearing.ClearStorage(address);
        clearing.SelfDestructedStorageAddresses[address] = false;
        PbtSnapshotContent writing = new();
        (writeInClearLayer ? clearing : writing).SetSlot(key, replacement);
        writing.Accounts[address] = null;
        PbtSnapshotContent[] layers = [older, clearLast ? writing : clearing, clearLast ? clearing : writing];
        PbtRetainedSnapshot[] sources = BuildRetained(store, layers);
        try
        {
            using PbtRetainedSnapshot merged = MergeRetained(store, sources, out _);
            DisposeSources(sources);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(merged.TryGetStorageClear(address, out bool clearValue), Is.True);
                Assert.That(clearValue, Is.False, "clear semantics depend on presence, not the stored bool");
                Assert.That(merged.TryGetAccount(address, out PbtAccount? account), Is.True);
                Assert.That(account, Is.Null);
                bool found = TryReadRun(merged, key, out PackedSlotRun? run);
                Assert.That(found, Is.EqualTo(!clearLast || writeInClearLayer));
                if (run is not null)
                {
                    Assert.That(run.Get(SlotRun.IndexOf(key)), Is.EqualTo(replacement));
                    Assert.That(run.Get(SlotRun.IndexOf(neighbor)), Is.EqualTo(UInt256.Zero));
                    SlotRun.Return(run);
                }
                Assert.That(TryReadRun(merged, differentRun, out PackedSlotRun? removed), Is.False);
                if (removed is not null) SlotRun.Return(removed);
                Assert.That(TryReadRun(merged, otherAddress, out PackedSlotRun? other), Is.True);
                Assert.That(other!.Get(SlotRun.IndexOf(otherAddress)), Is.EqualTo(original));
                SlotRun.Return(other!);
                Assert.That(merged.From, Is.EqualTo(StateId.PreGenesis));
                Assert.That(merged.To, Is.EqualTo(new StateId(3, TestItem.KeccakA.ValueHash256)));
                Assert.That(merged.TreeRoot, Is.EqualTo(TestItem.KeccakB.ValueHash256));
            }
        }
        finally { DisposeSources(sources); }
    }

    [Test]
    public void Retained_blob_frontier_reclaims_after_last_reader_and_is_reused()
    {
        using PbtRetainedTestStore store = new();
        long initialAllocated = Nethermind.State.Flat.Metrics.BlobAllocatedBytes;
        ushort? reusedArena = null;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            PbtSnapshotContent content = new();
            byte[] expected = new byte[65537];
            expected.AsSpan().Fill((byte)(iteration + 1));
            content.Codes[TestItem.KeccakA.ValueHash256] = new(expected);
            using PbtSnapshot memory = NewSnapshot(content, 0);
            PbtRetainedSnapshot[] sources = [store.Build(memory)];
            PbtRetainedSnapshot? merged = null;
            PbtRetainedSnapshot? reader = null;
            try
            {
                ushort arena = Owners(sources[0])[0];
                if (reusedArena is { } previous) Assert.That(arena, Is.EqualTo(previous));
                reusedArena = arena;
                BlobArenaFile file = store.Blobs.GetFile(arena);
                long frontier = Nethermind.State.Flat.Metrics.BlobAllocatedBytes - initialAllocated;
                Assert.That(frontier, Is.GreaterThan(0));
                merged = MergeRetained(store, sources, out _);
                DisposeSources(sources);
                Assert.That(Nethermind.State.Flat.Metrics.BlobAllocatedBytes - initialAllocated, Is.EqualTo(frontier), "the merged snapshot still owns the blob");
                Assert.That(merged.TryLease(), Is.True);
                reader = merged;
                merged.Dispose();
                merged = null;
                Assert.That(Nethermind.State.Flat.Metrics.BlobAllocatedBytes - initialAllocated, Is.EqualTo(frontier), "a held reader prevents reclaim");
                Assert.That(reader.TryGetCode(TestItem.KeccakA.ValueHash256, out CodeInfo? code), Is.True);
                Assert.That(code!.CodeSpan.ToArray(), Is.EqualTo(expected));
                reader.Dispose();
                reader = null;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(file.RandomRead(0, new byte[1]), Is.Zero, "final release truncates without a startup sweep");
                    Assert.That(Nethermind.State.Flat.Metrics.BlobAllocatedBytes, Is.EqualTo(initialAllocated));
                }
            }
            finally
            {
                reader?.Dispose();
                merged?.Dispose();
                DisposeSources(sources);
            }
        }
    }

    [Test]
    public void Retained_merge_selects_complete_payload_revision_and_exact_blob_owners(
        [Values(0, 254, 255, 65536, 65537)] int newestLength)
    {
        using PbtRetainedTestStore store = new();
        ValueHash256 codeHash = TestItem.KeccakA.ValueHash256;
        PbtSnapshotContent old = new();
        old.Codes[codeHash] = new(new byte[65537]);
        using PbtSnapshot oldMemory = NewSnapshot(old, 0);
        PbtRetainedSnapshot oldSource = store.Build(oldMemory);
        ushort oldArena = Owners(oldSource)[0];
        using (BlobArenaWriter rotate = store.Blobs.CreateWriter(4 * 1024 * 1024)) rotate.Complete();
        byte[] expected = new byte[newestLength];
        expected.AsSpan().Fill(0xA7);
        PbtSnapshotContent latest = new();
        latest.Codes[codeHash] = new(expected);
        using PbtSnapshot latestMemory = NewSnapshot(latest, 1);
        PbtRetainedSnapshot latestSource = store.Build(latestMemory);
        PbtRetainedSnapshot[] sources = [oldSource, latestSource];
        ushort[] selectedOwners = Owners(latestSource);
        try
        {
            PbtRetainedSnapshot merged = MergeRetained(store, sources, out _);
            try
            {
                Assert.That(Owners(merged), Is.EqualTo(selectedOwners));
                DisposeSources(sources);
                store.Blobs.SweepUnreferenced();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(store.Blobs.TryLeaseFile(oldArena, out _), Is.False, "unselected blobs are reclaimable after source release");
                    Assert.That(merged.TryGetCode(codeHash, out CodeInfo? code), Is.True);
                    Assert.That(code!.CodeSpan.ToArray(), Is.EqualTo(expected));
                    foreach (ushort id in selectedOwners)
                    {
                        Assert.That(store.Blobs.TryLeaseFile(id, out BlobArenaFile? file), Is.True);
                        file!.Dispose();
                    }
                }
            }
            finally { merged.Dispose(); }
            store.Blobs.SweepUnreferenced();
            Assert.That(store.Blobs.TryLeaseFile(oldArena, out _), Is.False);
            foreach (ushort id in selectedOwners) Assert.That(store.Blobs.TryLeaseFile(id, out _), Is.False);
        }
        finally { DisposeSources(sources); }
    }

    [Test]
    public void Retained_merge_preserves_groups_and_bloom_membership(
        [ValueSource(nameof(MergeGroupPaths))] PbtStorageNodePath group, [Values] bool tombstone)
    {
        using PbtRetainedTestStore store = new();
        PbtVariableTreeKey leaf = PbtTestLeaves.SlotKey(TestItem.AddressA, 64);
        PbtSnapshotContent older = new();
        PbtSnapshotContent newer = new();
        byte[] expected;
        using (PbtNodeGroupWriter<PbtStorageNodePath> writer = PbtNodeGroupWriter<PbtStorageNodePath>.Rent(group.BitDepth, store.Memory))
        {
            PbtTraversalPath path = PbtTraversalPath.FromPath(stackalloc byte[PbtVariableTreeKey.MaxLength], group);
            if (group.BitDepth != 0) path.AppendMut(0);
            byte[] node = group.BitDepth == 0 ? PbtTreeHarness.EncodeLeaf(leaf)
                : PbtTreeHarness.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
            writer.Write(path, group.BitDepth == 0 ? PbtThreeLevelGroupGeometry.RootPosition : 0, node);
            using RefCountingMemory payload = writer.Detach(default, ushort.MaxValue)!;
            expected = payload.GetSpan().ToArray();
            older.SetNodeGroup(group, payload);
            newer.SetNodeGroup(group, tombstone ? null : payload);
        }
        ValueHash256 address = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        newer.Accounts[address] = null;
        newer.ClearStorage(address);
        newer.SetSlot(leaf, default);
        PbtRetainedSnapshot[] sources = BuildRetained(store, [older, newer]);
        try
        {
            using PbtRetainedSnapshot merged = MergeRetained(store, sources, out _);
            DisposeSources(sources);
            Assert.That(merged.TryGetNodeGroup(group, out RefCountingMemory? payload), Is.True);
            using (payload) Assert.That(payload is null ? null : payload.GetSpan().ToArray(), Is.EqualTo(tombstone ? null : expected));
            using PbtRetainedScanner scanner = merged.Scan();
            while (scanner.MoveNext())
            {
                ReadOnlySpan<byte> key = scanner.Key;
                if (!PbtRetainedKey.IsEntity(key)) continue;
                Assert.That(merged.BloomRef.Filter.MightContain(PbtRetainedKey.BloomHash(key)), Is.True);
            }
        }
        finally { DisposeSources(sources); }
        Assert.That(TrackingMemoryProvider.CountUnreleased(store.Memory.Rented), Is.Zero);
    }

    [Test]
    public void Retained_merge_preserves_full_or_explicit_empty_runs([Values(0u, 1024u)] uint firstSlot, [Values] bool empty)
    {
        using PbtRetainedTestStore store = new();
        PbtSnapshotContent older = new();
        PbtSnapshotContent newer = new();
        PbtVariableTreeKey firstKey = PbtTestLeaves.SlotKey(TestItem.AddressA, firstSlot);
        for (uint index = 0; index < 16; index++)
        {
            PbtVariableTreeKey key = PbtTestLeaves.SlotKey(TestItem.AddressA, firstSlot + index);
            older.SetSlot(key, (UInt256)1);
            newer.SetSlot(key, empty ? default : (UInt256)(index + 2));
        }
        PbtRetainedSnapshot[] sources = BuildRetained(store, [older, newer]);
        try
        {
            using PbtRetainedSnapshot merged = MergeRetained(store, sources, out _);
            DisposeSources(sources);
            Assert.That(TryReadRun(merged, firstKey, out PackedSlotRun? run), Is.True);
            try
            {
                Assert.That(run!.Count, Is.EqualTo(empty ? 0 : 16));
                for (int index = 0; index < 16; index++)
                    Assert.That(run.Get(index), Is.EqualTo(empty ? default : (UInt256)(index + 2)));
            }
            finally { SlotRun.Return(run!); }
            Assert.That(merged.BloomRef.Filter.MightContain(PbtRetainedKey.BloomHash(firstKey.Bytes[0] == Eip8297KeyDerivation.AccountZone
                ? PbtRetainedKey.Run(SlotRun.RunKey(PbtPath.Create(firstKey.Bytes)))
                : PbtRetainedKey.Run(SlotRun.RunKey(PbtStoragePath.Create(firstKey.Bytes))))), Is.True);
        }
        finally { DisposeSources(sources); }
    }

    private static readonly PbtStorageNodePath[] MergeGroupPaths =
        [new([], 0), new([0], 3), new([0], 6), new([1, 0], 9), new([0xFF, 0], 9)];

    [Test]
    public void Retained_merge_cancellation_releases_source_scanners_and_sessions()
    {
        using PbtRetainedTestStore store = new();
        PbtSnapshotContent content = new();
        content.Codes[TestItem.KeccakA.ValueHash256] = new(new byte[65537]);
        using PbtSnapshot memory = NewSnapshot(content, 0);
        PbtRetainedSnapshot source = store.Build(memory);
        ushort arena = Owners(source)[0];
        using CancellationTokenSource cancellation = new();
        CancelingWriter writer = new(cancellation);
        using BloomFilter bloom = new(16, 14);
        try
        {
            Assert.Throws<OperationCanceledException>(() => PbtRetainedSnapshotMerger.Merge(
                [source], new(source.From, source.To, source.TreeRoot), ref writer, bloom, cancellation.Token));
            Assert.That(source.TryGetCode(TestItem.KeccakA.ValueHash256, out CodeInfo? code), Is.True);
            Assert.That(code!.CodeSpan.Length, Is.EqualTo(65537));
        }
        finally { source.Dispose(); }
        store.Blobs.SweepUnreferenced();
        Assert.That(store.Blobs.TryLeaseFile(arena, out _), Is.False);
    }

    private sealed class CancelingWriter(CancellationTokenSource cancellation) : IByteBufferWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        public Span<byte> GetSpan(int sizeHint) => _buffer.GetSpan(sizeHint);
        public void Advance(int count)
        {
            _buffer.Advance(count);
            cancellation.Cancel();
        }
        public long Written => _buffer.WrittenCount;
        public long FirstOffset => 0;
    }

    [Test]
    public void Retained_merge_rejects_nonadjacent_or_incorrect_metadata([Values] bool invalidMetadata)
    {
        using PbtRetainedTestStore store = new();
        using PbtSnapshot firstMemory = NewSnapshot(new(), 0);
        using PbtSnapshot nextMemory = NewSnapshot(new(), invalidMetadata ? 1 : 2);
        using PbtRetainedSnapshot first = store.Build(firstMemory);
        using PbtRetainedSnapshot next = store.Build(nextMemory);
        PbtRetainedSnapshot[] sources = [first, next];
        PbtRetainedMetadata metadata = new(first.From, next.To, invalidMetadata ? default : next.TreeRoot);
        using ArenaWriter writer = store.Arena.CreateWriter(16384);
        using BloomFilter bloom = new(16, 14);
        Assert.Throws<InvalidOperationException>(() => MergeInvalid());
        void MergeInvalid() => PbtRetainedSnapshotMerger.Merge(sources, metadata, ref writer.GetWriter(), bloom, CancellationToken.None);
    }

    [Test]
    public void Retained_merge_streams_large_payloads_without_deserializing_window()
    {
        using PbtRetainedTestStore store = new();
        PbtRetainedSnapshot[] sources = new PbtRetainedSnapshot[12];
        long payloadBytes = 0;
        try
        {
            for (int layer = 0; layer < sources.Length; layer++)
            {
                PbtSnapshotContent content = new();
                for (int entity = 0; entity < 8; entity++)
                {
                    byte[] hash = new byte[32];
                    hash[^2] = (byte)layer;
                    hash[^1] = (byte)entity;
                    byte[] bytes = new byte[65537];
                    bytes.AsSpan().Fill((byte)layer);
                    content.Codes[new ValueHash256(hash)] = new(bytes);
                    payloadBytes += bytes.Length;
                }
                using PbtSnapshot snapshot = NewSnapshot(content, layer);
                sources[layer] = store.Build(snapshot);
            }
            using PbtRetainedSnapshot merged = MergeRetained(store, sources, out long allocated);
            TestContext.Out.WriteLine($"Retained merge managed allocation {allocated} bytes for {payloadBytes} source payload bytes.");
            Assert.That(allocated, Is.LessThan(128 * 1024));
            Assert.That(allocated, Is.LessThan(payloadBytes / 20));
            byte[] lastHash = new byte[32];
            lastHash[^2] = 11;
            lastHash[^1] = 7;
            Assert.That(merged.TryGetCode(new ValueHash256(lastHash), out CodeInfo? code), Is.True);
            Assert.That(code!.CodeSpan.Length, Is.EqualTo(65537));
        }
        finally { DisposeSources(sources); }
    }

    private PbtSnapshot NewSnapshot(PbtSnapshotContent content, int index) => new(
        index == 0 ? StateId.PreGenesis : new StateId((ulong)index, TestItem.KeccakA.ValueHash256),
        new StateId((ulong)index + 1, TestItem.KeccakA.ValueHash256), TestItem.KeccakB.ValueHash256,
        content, _pool, PbtResourcePool.Usage.MainBlockProcessing);

    private PbtRetainedSnapshot[] BuildRetained(PbtRetainedTestStore store, PbtSnapshotContent[] contents)
    {
        PbtRetainedSnapshot[] result = new PbtRetainedSnapshot[contents.Length];
        try
        {
            for (int index = 0; index < contents.Length; index++)
            {
                using PbtSnapshot snapshot = NewSnapshot(contents[index], index);
                result[index] = store.Build(snapshot);
            }
            return result;
        }
        catch { DisposeSources(result); throw; }
    }

    private static PbtRetainedSnapshot MergeRetained(PbtRetainedTestStore store, PbtRetainedSnapshot[] sources, out long allocated)
    {
        long estimate = 16384;
        foreach (PbtRetainedSnapshot source in sources) estimate += source.Size;
        using ArenaWriter writer = store.Arena.CreateWriter(estimate);
        using RefCountedBloomFilter bloom = new(new BloomFilter(1024, 14));
        PbtRetainedMetadata metadata = new(sources[0].From, sources[^1].To, sources[^1].TreeRoot);
        long before = GC.GetAllocatedBytesForCurrentThread();
        PbtRetainedSnapshotMerger.Merge(sources, metadata, ref writer.GetWriter(), bloom.Filter, CancellationToken.None);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        (SnapshotLocation location, ArenaReservation reservation) = writer.Complete();
        using (reservation)
        {
            reservation.Fsync();
            return new(new(metadata.From, metadata.To, location, SnapshotTier.PersistedSmallCompacted), reservation, store.Blobs, store.Memory, bloom);
        }
    }

    private static bool TryReadRun(PbtRetainedSnapshot snapshot, PbtVariableTreeKey slot, out PackedSlotRun? run) =>
        slot.Bytes[0] == Eip8297KeyDerivation.AccountZone
            ? snapshot.TryGetSlotRun(SlotRun.RunKey(PbtPath.Create(slot.Bytes)), out run)
            : snapshot.TryGetSlotRun(SlotRun.RunKey(PbtStoragePath.Create(slot.Bytes)), out run);

    private static ushort[] Owners(PbtRetainedSnapshot snapshot)
    {
        List<ushort> result = [];
        using PbtRetainedScanner scanner = snapshot.Scan();
        while (scanner.MoveNext())
            if (scanner.Key[0] == PbtRetainedKey.Ownership)
                result.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(scanner.Key[1..]));
        return result.ToArray();
    }

    private static void DisposeSources(PbtRetainedSnapshot[] sources)
    {
        for (int index = 0; index < sources.Length; index++)
        {
            sources[index]?.Dispose();
            sources[index] = null!;
        }
    }

    [TestCase(4, 0, 2, 0, SnapshotTier.PersistedSmallCompacted, 0)]
    [TestCase(4, 0, 4, 0, SnapshotTier.PersistedCompactSized, 0)]
    [TestCase(4, 0, 8, 0, SnapshotTier.PersistedLargeCompacted, 0)]
    [TestCase(4, 0, 8, 2, SnapshotTier.PersistedLargeCompacted, 2)]
    [TestCase(4, 3, 5, 0, SnapshotTier.PersistedCompactSized, 1)]
    [TestCase(4, 7, 1, -1, SnapshotTier.PersistedCompactSized, -1)]
    [TestCase(4, 3, 5, -1, SnapshotTier.PersistedLargeCompacted, -1)]
    [TestCase(1, 0, 8, 0, SnapshotTier.PersistedLargeCompacted, 0)]
    public async Task Retained_compactor_uses_shared_schedule_ranges_and_tiers(int compactSize, int offset, int target,
        int floor, SnapshotTier expectedTier, int expectedFrom)
    {
        using RetainedCompactionContext context = new(compactSize, offset);
        context.AddHistory(target, includeGenesis: expectedFrom < 0);
        await context.CompactAsync(unchecked((ulong)floor), target);
        Assert.That(context.Repository.TryLeaseRetained(CompactionState(target), target - expectedFrom, expectedTier, out PbtRetainedSnapshot? output), Is.True);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(output!.From, Is.EqualTo(expectedFrom < 0 ? StateId.PreGenesis : CompactionState(expectedFrom)));
                Assert.That(output.To, Is.EqualTo(CompactionState(target)));
                Assert.That(output.TreeRoot, Is.EqualTo(TestItem.KeccakB.ValueHash256));
                Assert.That(output.TryGetCode(TestItem.KeccakC.ValueHash256, out CodeInfo? code), Is.True);
                Assert.That(code!.CodeSpan[0], Is.EqualTo((byte)target));
            }
        }
    }

    [Test]
    public async Task Retained_compactor_skips_genesis_single_edges_and_clamped_windows()
    {
        using RetainedCompactionContext context = new(4, 0);
        context.AddHistory(8, includeGenesis: true);
        await context.CompactAsync(5, 0, 1, 2, 6);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Loader.Publications, Is.Empty);
            Assert.That(context.Repository.RetainedCount, Is.EqualTo(9));
        }
    }

    [Test]
    public async Task Retained_compactor_batch_builds_narrower_before_boundaries_and_drains_both_tiers()
    {
        using RetainedCompactionContext context = new(4, 0);
        context.AddHistory(8);
        CountingStatePool pool = new();
        ArrayPoolList<StateId> batch = new(pool, 8);
        // Reverse admission makes ordering a property of scheduling rather than the input list.
        for (int block = 8; block >= 1; block--) batch.Add(CompactionState(block));
        await context.Compactor.EnqueueAsync(batch, 0, CancellationToken.None);
        await Task.WhenAll(context.Compactor.DisposeAsync().AsTask(), context.Compactor.DisposeAsync().AsTask());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Returns, Is.EqualTo(1));
            Assert.That(context.Repository.HasRetained(CompactionState(2), 2L, SnapshotTier.PersistedSmallCompacted), Is.True);
            Assert.That(context.Repository.HasRetained(CompactionState(8), 4L, SnapshotTier.PersistedCompactSized), Is.True);
            Assert.That(context.Repository.HasRetained(CompactionState(8), 8L, SnapshotTier.PersistedLargeCompacted), Is.True);
        }
        (SnapshotTier Tier, long[] Widths)[] publications = context.Loader.Publications.ToArray();
        Assert.That(publications.Where(p => p.Tier == SnapshotTier.PersistedCompactSized).Any(p => p.Widths.Contains(2)), Is.True);
        Assert.That(publications.Single(p => p.Tier == SnapshotTier.PersistedLargeCompacted).Widths, Does.Contain(4));
        using ArrayPoolList<StateId> rejected = new(1);
        Assert.That(async () => await context.Compactor.EnqueueAsync(rejected, 0, new CancellationToken(true)), Throws.InstanceOf<ObjectDisposedException>());
    }

    [TestCase(2, SnapshotTier.PersistedSmallCompacted)]
    [TestCase(4, SnapshotTier.PersistedCompactSized)]
    [TestCase(8, SnapshotTier.PersistedLargeCompacted)]
    public async Task Retained_compactor_failed_job_does_not_skip_later_work_in_batch(int failedBlock, SnapshotTier failedTier)
    {
        using RetainedCompactionContext context = new(4, 0);
        context.AddHistory(16);
        int failures = 0;
        context.Loader.BeforePublish = (output, _) =>
        {
            if (output.To == CompactionState(failedBlock) && output.Tier == failedTier && Interlocked.Increment(ref failures) == 1)
                throw new IOException("Injected compactor publication failure");
        };
        ArrayPoolList<StateId> batch = new(4);
        batch.AddRange(CompactionState(2), CompactionState(4), CompactionState(8), CompactionState(16));
        await context.Compactor.EnqueueAsync(batch, 0, CancellationToken.None);
        await context.Compactor.DisposeAsync();
        Assert.That(context.Repository.HasRetained(CompactionState(16), 16L, SnapshotTier.PersistedLargeCompacted), Is.True);
        Assert.That(failures, Is.EqualTo(1));
    }

    [Test]
    public async Task Retained_compactor_bounded_queue_releases_canceled_and_queued_batches([Values] bool processExit)
    {
        using CancellationTokenSource exit = new();
        using RetainedCompactionContext context = new(4, 0, exit.Token);
        context.AddHistory(2);
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Loader.BeforePublish = (_, _) => { entered.TrySetResult(); release.Wait(); };
        CountingStatePool pool = new();
        ArrayPoolList<StateId> first = new(pool, 1) { CompactionState(2) };
        await context.Compactor.EnqueueAsync(first, 0, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 16; i++) await context.Compactor.EnqueueAsync(new(pool, 1), 0, CancellationToken.None);
            using CancellationTokenSource producer = new();
            Task pending = context.Compactor.EnqueueAsync(new(pool, 1), 0, producer.Token).AsTask();
            Assert.That(pending.IsCompleted, Is.False);
            if (processExit) exit.Cancel();
            else producer.Cancel();
            Assert.That(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        }
        finally { release.Set(); }
        await context.Compactor.DisposeAsync();
        Assert.That(pool.Returns, Is.EqualTo(18));
    }

    [Test]
    public async Task Retained_compactor_does_not_publish_a_source_pruned_during_merge()
    {
        using RetainedCompactionContext context = new(4, 0);
        context.AddHistory(2);
        context.Loader.BeforePublish = (_, sources) => context.Repository.RemoveRetainedStatesBefore(sources[0].To.BlockNumber + 1);
        await context.CompactAsync(0, 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Loader.Publications, Is.Empty);
            Assert.That(context.Repository.RetainedCount, Is.EqualTo(1), "only the base at block 2 remains");
        }
    }

    [Test]
    public async Task Retained_compactor_large_merge_does_not_block_later_compact_sized_work()
    {
        using RetainedCompactionContext context = new(4, 0);
        context.AddHistory(12);
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource largeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource laterPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Loader.BeforePublish = (output, _) =>
        {
            if (output.Tier == SnapshotTier.PersistedLargeCompacted)
            {
                largeEntered.TrySetResult();
                release.Wait();
            }
            if (output.To == CompactionState(12) && output.Tier == SnapshotTier.PersistedCompactSized) laterPublished.TrySetResult();
        };
        try
        {
            await context.Compactor.EnqueueAsync(new ArrayPoolList<StateId>(1) { CompactionState(8) }, 0, CancellationToken.None);
            await largeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await context.Compactor.EnqueueAsync(new ArrayPoolList<StateId>(1) { CompactionState(12) }, 0, CancellationToken.None);
            await laterPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); }
        await context.Compactor.DisposeAsync();
        Assert.That(context.Repository.HasRetained(CompactionState(8), 4L, SnapshotTier.PersistedCompactSized), Is.True);
        Assert.That(context.Repository.HasRetained(CompactionState(8), 8L, SnapshotTier.PersistedLargeCompacted), Is.True);
    }

    [Test]
    public async Task Retained_compactor_uses_four_independent_large_workers()
    {
        using RetainedCompactionContext context = new(4, 0);
        context.AddHistory(40);
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource fourEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource fifthCompactSized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        context.Loader.BeforePublish = (output, _) =>
        {
            if (output.Tier == SnapshotTier.PersistedLargeCompacted)
            {
                if (Interlocked.Increment(ref started) == 4) fourEntered.TrySetResult();
                release.Wait();
            }
            if (output.To == CompactionState(40) && output.Tier == SnapshotTier.PersistedCompactSized) fifthCompactSized.TrySetResult();
        };
        try
        {
            ArrayPoolList<StateId> batch = new(4);
            batch.AddRange(CompactionState(8), CompactionState(16), CompactionState(24), CompactionState(32));
            await context.Compactor.EnqueueAsync(batch, 0, CancellationToken.None);
            await fourEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await context.Compactor.EnqueueAsync(new ArrayPoolList<StateId>(1) { CompactionState(40) }, 0, CancellationToken.None);
            await fifthCompactSized.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(Volatile.Read(ref started), Is.EqualTo(4));
        }
        finally { release.Set(); }
        await context.Compactor.DisposeAsync();
        Assert.That(started, Is.EqualTo(5));
    }

    [Test]
    public async Task Retained_compactor_respects_retained_maximum_window()
    {
        using RetainedCompactionContext context = new(4, 0, maxCompactSize: 8);
        context.AddHistory(16);
        await context.CompactAsync(0, 16);
        Assert.That(context.Repository.TryLeaseRetained(CompactionState(16), 8, SnapshotTier.PersistedLargeCompacted, out PbtRetainedSnapshot? output), Is.True);
        using (output) Assert.That(output!.From, Is.EqualTo(CompactionState(8)));
    }

    private static StateId CompactionState(int number) => new((ulong)number, TestItem.KeccakA.ValueHash256);

    private sealed class CountingStatePool : ArrayPool<StateId>
    {
        private int _returns;
        internal int Returns => Volatile.Read(ref _returns);
        public override StateId[] Rent(int minimumLength) => new StateId[minimumLength];
        public override void Return(StateId[] array, bool clearArray = false) => Interlocked.Increment(ref _returns);
    }

    private sealed class RecordingRetainedLoader(IPbtRetainedSnapshotLoader inner) : IPbtRetainedSnapshotLoader
    {
        private readonly object _lock = new();
        internal List<(SnapshotTier Tier, long[] Widths)> Publications { get; } = [];
        internal Action<PbtRetainedSnapshot, PbtRetainedSnapshot[]>? BeforePublish { get; set; }
        public void Load() => inner.Load();
        public bool ConvertAndRegister(PbtSnapshot snapshot) => inner.ConvertAndRegister(snapshot);
        public bool RegisterCompacted(PbtRetainedSnapshot snapshot, ReadOnlySpan<PbtRetainedSnapshot> sources)
        {
            BeforePublish?.Invoke(snapshot, sources.ToArray());
            bool result = inner.RegisterCompacted(snapshot, sources);
            if (result)
                lock (_lock) Publications.Add((snapshot.Tier, sources.ToArray().Select(s => unchecked((long)(s.To.BlockNumber - s.From.BlockNumber))).ToArray()));
            return result;
        }
        public void Dispose() { }
    }

    [Test]
    public async Task Retained_compactor_accepts_same_storage_bloom_twins_after_gathering_sources()
    {
        using RetainedCompactionContext context = new(4, 0);
        context.AddHistory(8);
        bool rebound = false;
        context.Loader.BeforePublish = (output, sources) =>
        {
            if (output.Tier != SnapshotTier.PersistedLargeCompacted) return;
            using RefCountedBloomFilter shared = new(new BloomFilter(32, 14));
            context.Repository.ShareBloomAcrossRange(output.From, output.To, shared);
            Assert.That(sources.Any(source => !context.Repository.ContainsRetainedSource(source)), Is.True);
            Assert.That(sources.All(context.Repository.ContainsRetainedStorageSource), Is.True);
            rebound = true;
        };
        await context.CompactAsync(0, 8);
        Assert.That(rebound, Is.True);
        Assert.That(context.Repository.HasRetained(CompactionState(8), 8L, SnapshotTier.PersistedLargeCompacted), Is.True);
    }

    [Test]
    public async Task Retained_large_bloom_is_shared_and_reconstructed_after_restart([Values] bool enabled)
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-shared-bloom-" + Guid.NewGuid().ToString("N"));
        PbtConfig config = new()
        {
            Enabled = true,
            CompactSize = 4,
            CompactionOffset = 0,
            PersistedSnapshotBloomBitsPerKey = enabled ? 14 : 0,
            ArenaFileSizeBytes = 1048576,
            PersistedSnapshotDedicatedArenaThresholdBytes = 1048576,
            PersistedSnapshotArenaPageCacheBytes = 0,
        };
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                await using IContainer container = PbtTestContext.BuildProductionContainer(config,
                    builder => builder.AddSingleton<IDbFactory, Nethermind.Db.Rocks.RocksDbFactory>(), new InitConfig { BaseDbPath = path });
                IPbtRetainedSnapshotLoader loader = container.Resolve<IPbtRetainedSnapshotLoader>();
                loader.Load();
                PbtSnapshotRepository repository = container.Resolve<PbtSnapshotRepository>();
                if (pass == 0)
                {
                    IPbtResourcePool pool = container.Resolve<IPbtResourcePool>();
                    for (int block = 1; block <= 8; block++)
                    {
                        PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
                        ValueHash256 address = PbtStateKey.AddressKeyHash(TestItem.AddressA);
                        if (block == 1)
                        {
                            content.SetSlot(PbtTestLeaves.SlotKey(TestItem.AddressA, 7), (UInt256)1);
                            content.SetSlot(PbtTestLeaves.SlotKey(TestItem.AddressA, 1024), (UInt256)2);
                            content.Accounts[address] = null;
                            foreach (PbtStorageNodePath group in MergeGroupPaths) content.SetNodeGroup(group, null);
                        }
                        if (block == 2) content.ClearStorage(address);
                        content.Codes[TestItem.KeccakC.ValueHash256] = new(new byte[65537]);
                        PbtSnapshot snapshot = new(CompactionState(block - 1), CompactionState(block), TestItem.KeccakB.ValueHash256,
                            content, pool, PbtResourcePool.Usage.MainBlockProcessing);
                        snapshot.TryLease(); repository.TryAdd(snapshot);
                        using (snapshot) Assert.That(loader.ConvertAndRegister(snapshot), Is.True);
                    }
                    IPbtRetainedSnapshotCompactor compactor = container.Resolve<IPbtRetainedSnapshotCompactor>();
                    ArrayPoolList<StateId> batch = new(8);
                    for (int block = 1; block <= 8; block++) batch.Add(CompactionState(block));
                    await compactor.EnqueueAsync(batch, 0, CancellationToken.None);
                    await compactor.DisposeAsync();
                }
                Assert.That(repository.TryLeaseRetained(CompactionState(8), 8, SnapshotTier.PersistedLargeCompacted, out PbtRetainedSnapshot? widest), Is.True);
                using (widest)
                {
                    for (int block = 1; block <= 8; block++)
                    {
                        Assert.That(repository.TryLeaseRetained(CompactionState(block), 1, SnapshotTier.PersistedBase, out PbtRetainedSnapshot? retained), Is.True);
                        using (retained)
                        {
                            Assert.That(ReferenceEquals(retained!.BloomRef, widest!.BloomRef), Is.EqualTo(enabled || pass == 0));
                            using PbtRetainedScanner scanner = retained.Scan();
                            while (scanner.MoveNext())
                                if (PbtRetainedKey.IsEntity(scanner.Key))
                                    Assert.That(retained.BloomRef.Filter.MightContain(PbtRetainedKey.BloomHash(scanner.Key)), Is.True);
                        }
                    }
                    Assert.That(widest!.TreeRoot, Is.EqualTo(TestItem.KeccakB.ValueHash256));
                }
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    private sealed class RetainedCompactionContext : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "pbt-retained-compaction-" + Guid.NewGuid().ToString("N"));
        private readonly IContainer _container;
        internal PbtSnapshotRepository Repository => _container.Resolve<PbtSnapshotRepository>();
        internal RecordingRetainedLoader Loader { get; }
        internal PbtRetainedSnapshotCompactor Compactor => (PbtRetainedSnapshotCompactor)_container.Resolve<IPbtRetainedSnapshotCompactor>();

        internal RetainedCompactionContext(int compactSize, int offset, CancellationToken exit = default, ulong maxCompactSize = 1048576)
        {
            RecordingRetainedLoader? loader = null;
            _container = PbtTestContext.BuildProductionContainer(new PbtConfig
            {
                Enabled = true,
                CompactSize = compactSize,
                CompactionOffset = offset,
                PersistedSnapshotMaxCompactSize = maxCompactSize,
                ArenaFileSizeBytes = 1048576,
                PersistedSnapshotDedicatedArenaThresholdBytes = 1048576,
                PersistedSnapshotArenaPageCacheBytes = 0,
            }, builder =>
            {
                builder.RegisterDecorator<IPbtRetainedSnapshotLoader>((_, _, inner) => loader = new(inner));
                builder.RegisterInstance(new CompactionExitSource(exit)).As<IProcessExitSource>();
            }, new InitConfig { BaseDbPath = _path });
            _container.Resolve<IPbtRetainedSnapshotLoader>().Load();
            Loader = loader!;
        }

        /// <summary>Compacts <paramref name="blocks"/> as one batch and waits for every job it schedules, which retires the compactor.</summary>
        internal async Task CompactAsync(ulong persistedBlockNumber, params int[] blocks)
        {
            ArrayPoolList<StateId> batch = new(blocks.Length);
            foreach (int block in blocks) batch.Add(CompactionState(block));
            await Compactor.EnqueueAsync(batch, persistedBlockNumber, CancellationToken.None);
            await Compactor.DisposeAsync();
        }

        internal void AddHistory(int last, bool includeGenesis = false, int start = 1)
        {
            IPbtResourcePool pool = _container.Resolve<IPbtResourcePool>();
            for (int block = includeGenesis ? 0 : start; block <= last; block++)
            {
                PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
                byte[] code = new byte[65537];
                code.AsSpan().Fill((byte)block);
                content.Codes[TestItem.KeccakC.ValueHash256] = new(code);
                PbtSnapshot snapshot = new(block == 0 ? StateId.PreGenesis : CompactionState(block - 1), CompactionState(block),
                    TestItem.KeccakB.ValueHash256, content, pool, PbtResourcePool.Usage.MainBlockProcessing);
                snapshot.TryLease();
                Repository.TryAdd(snapshot);
                using (snapshot) Assert.That(Loader.ConvertAndRegister(snapshot), Is.True);
            }
        }

        public void Dispose()
        {
            _container.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class CompactionExitSource(CancellationToken token) : IProcessExitSource
    {
        public CancellationToken Token => token;
        public void Exit(int exitCode) { }
    }

    private PbtSnapshot Compact(params PbtSnapshotContent[] layersOldestFirst)
    {
        using PbtSnapshotPooledList chain = PbtSnapshotBundleTestExtensions.Chain(_pool, layersOldestFirst);
        return NewCompactor().Compact(chain);
    }

    private PbtSnapshotCompactor NewCompactor() => new(_pool, PbtCoreRegistration.CreateCompactionSchedule(new Nethermind.Db.MemDb(), Config, Nethermind.Logging.LimboLogs.Instance), new PbtSnapshotRepository(new Nethermind.Monitoring.Config.MetricsConfig()), Config, Nethermind.Logging.LimboLogs.Instance);
}
