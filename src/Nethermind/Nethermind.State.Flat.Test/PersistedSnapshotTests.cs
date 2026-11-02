// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistedSnapshotTests
{
    private ResourcePool _resourcePool = null!;
    private ArenaManager _memArena = null!;
    private string _memArenaDir = null!;
    private BlobArenaManager _blobs = null!;
    private string _blobsDir = null!;
    private NodeRecordBuffer _buffer = null!;

    [SetUp]
    public void SetUp()
    {
        _resourcePool = new ResourcePool(new FlatDbConfig());
        _buffer = new NodeRecordBuffer(0, 64 * 1024);
        _memArenaDir = Path.Combine(Path.GetTempPath(), $"nm-pstest-arena-{Guid.NewGuid():N}");
        _memArena = TestFixtureHelpers.CreateArenaManager(_memArenaDir);
        _blobsDir = Path.Combine(Path.GetTempPath(), $"nm-pstest-blobs-{Guid.NewGuid():N}");
        _blobs = new BlobArenaManager(_blobsDir, 4L * 1024 * 1024);
    }

    [TearDown]
    public void TearDown()
    {
        _blobs.Dispose();
        _memArena.Dispose();
        _buffer.Dispose();
        try { Directory.Delete(_blobsDir, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_memArenaDir, recursive: true); } catch { /* best-effort */ }
    }

    private PersistedSnapshot CreatePersistedSnapshot(StateId from, StateId to, byte[] data) =>
        TestFixtureHelpers.CreatePersistedSnapshot(_memArena, _blobs, from, to, data);

    [Test]
    public void Serialization_uses_a_wrapped_retain_owner([Values] bool storage)
    {
        using ForwardingNodeRecordBuffer buffer = new(FlatTestHelpers.NewTestBuffer());
        using (Snapshot snapshot = FlatTestHelpers.MakeSnapshot(_resourcePool, buffer, content =>
        {
            if (storage) content.SetStorageNodeRlp(buffer, TestItem.KeccakA, TreePath.Empty, FlatTestHelpers.LeafRlpA);
            else content.SetStateNodeRlp(buffer, TreePath.Empty, FlatTestHelpers.LeafRlpA);
        }))
        {
            using INodeRecordBufferPin copy = snapshot.BufferPin!.Copy();
            using PersistedSnapshot persisted = CreatePersistedSnapshot(snapshot.From, snapshot.To,
                PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs));
            bool found = storage
                ? persisted.TryLoadStorageNodeRlp(TestItem.KeccakA.ValueHash256, TreePath.Empty, out byte[]? rlp)
                : persisted.TryLoadStateNodeRlp(TreePath.Empty, out rlp);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.True);
                Assert.That(rlp, Is.EqualTo(Bytes.FromHexString(FlatTestHelpers.LeafRlpA)));
                Assert.That(copy.Buffer, Is.SameAs(buffer));
                Assert.That(copy.WriteBoundary, Is.EqualTo(snapshot.BufferPin.WriteBoundary));
            }
        }
        Assert.That(buffer.OutstandingPins, Is.Zero);
    }

    [Test]
    public void Nested_snapshot_sorting_preserves_all_node_buckets([Range(1, 2)] int budget)
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("scoped-snapshot"));
        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        for (int i = 0; i < 256; i++)
        {
            Hash256 hash = Keccak.Compute(i.ToString());
            TreePath path = FlatTestHelpers.CanonicalPath(hash, (i % 3) switch { 0 => 3, 1 => 8, _ => 20 });
            content.SetStateNodeRlp(_buffer, path, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x80));
            content.SetStorageNodeRlp(_buffer, TestItem.KeccakA, path, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x80));
        }
        using Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        using ParallelUnbalancedWork.WorkerScope workers = ParallelUnbalancedWork.BeginWorkerScope(budget);

        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        Assert.DoesNotThrow(() => PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted));
    }

    [Test]
    public void Loader_sizes_reachable_nodes_independently_of_root_metadata([Values] bool storage)
    {
        using FlatTestContainer tier = new(new FlatDbConfig { PersistedSnapshotDedicatedArenaThresholdBytes = 1 },
            arenaFileSizeBytes: 4096, blobFileSizeBytes: 4 * 1024 * 1024);
        using INodeRecordBufferPin original = _buffer.PinHead();
        Hash256? address = storage ? TestItem.KeccakA : null;
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(512);
        for (int i = 0; i < 512; i++)
            entries.Add(new PatriciaTree.BulkSetEntry(Keccak.Compute(BitConverter.GetBytes(i)).ValueHash256, new byte[32]));
        (ValueHash256 Hash, ulong Pointer) root = FlatTestHelpers.BulkSet(_resourcePool, _buffer, address, (ValueKeccak.EmptyTreeHash, 0), entries, canParallelize: true);
        FlatTestHelpers.StartNewGeneration(_buffer);
        using Snapshot snapshot = new(StateId.PreGenesis, new StateId(1, root.Hash), new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing, original.Copy());
        if (storage) snapshot.Content.StorageRoots[address!] = root.Pointer;
        else
        {
            snapshot.Content.StateRootPointer = root.Pointer;
        }

        using PersistedSnapshot persisted = tier.ConvertToPersistedBase(snapshot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.EstimateMemory(), Is.LessThan(256), "only root metadata is retained");
            Assert.That(persisted.Size, Is.GreaterThan(4096), "fixed arena must accommodate the reachable node table");
            Assert.DoesNotThrow(() => PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted, dumpWhenFailed: false));
        }
    }

    [Test]
    public void IncrementalSnapshotsPersistOnlyChangedNodesAndReadBackTheCompleteTrie([Values] bool storage)
    {
        Hash256? address = storage ? TestItem.KeccakA : null;
        Hash256[] keys = Enumerable.Range(0, 256).Select(i => Keccak.Compute(BitConverter.GetBytes(i))).ToArray();
        INodeRecordBufferPin firstRetain = _buffer.PinHead();
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(keys.Length);
        foreach (Hash256 key in keys) entries.Add(new(key.ValueHash256, new byte[32]));
        (ValueHash256 Hash, ulong Pointer) firstRoot = FlatTestHelpers.BulkSet(_resourcePool, _buffer, address, (ValueKeccak.EmptyTreeHash, 0), entries, canParallelize: true);
        using Snapshot first = Create(0, ValueKeccak.EmptyTreeHash, firstRoot, firstRetain);
        INodeRecordBufferPin secondRetain = _buffer.PinHead();
        entries.Clear();
        entries.Add(new(keys[0].ValueHash256, Enumerable.Repeat((byte)1, 32).ToArray()));
        (ValueHash256 Hash, ulong Pointer) secondRoot = FlatTestHelpers.BulkSet(_resourcePool, _buffer, address, firstRoot, entries, canParallelize: false);
        using Snapshot second = Create(1, firstRoot.Hash, secondRoot, secondRetain);
        int firstCount = storage ? first.StorageNodes.Count() : first.StateNodes.Count();
        int secondCount = storage ? second.StorageNodes.Count() : second.StateNodes.Count();
        using PersistedSnapshotStack persisted = new(new(2)
        {
            CreatePersistedSnapshot(first.From, first.To, PersistedSnapshotBuilderTestExtensions.Build(first, _blobs)),
            CreatePersistedSnapshot(second.From, second.To, PersistedSnapshotBuilderTestExtensions.Build(second, _blobs))
        }, recordDetailedMetrics: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((firstRetain.WriteBoundary >> NodeRecordBuffer.OffsetBits), Is.EqualTo((secondRetain.WriteBoundary >> NodeRecordBuffer.OffsetBits)));
            Assert.That(secondCount, Is.InRange(1, 16));
            Assert.That(firstCount, Is.GreaterThan(secondCount * 8));
            foreach ((TreePath path, _, NodeRecord record) in _buffer.EnumerateNodes(secondRoot.Pointer, 0))
            {
                bool found = storage ? persisted.TryLoadStorageRlp(address!, path, out byte[]? rlp) : persisted.TryLoadStateRlp(path, out rlp);
                Assert.That(found, Is.True, path.ToString());
                Assert.That(rlp, Is.EqualTo(record.Rlp.ToArray()), path.ToString());
            }
        }

        Snapshot Create(ulong block, ValueHash256 from, (ValueHash256 Hash, ulong Pointer) root, INodeRecordBufferPin retain)
        {
            Snapshot snapshot = new(new(block, from), new(block + 1, root.Hash), new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing, retain);
            if (storage) snapshot.Content.StorageRoots[address!] = root.Pointer;
            else
            {
                snapshot.Content.StateRootPointer = root.Pointer;
            }
            return snapshot;
        }
    }

    [Test]
    public void Roundtrip_stops_at_persisted_child_boundaries([Values] bool storage)
    {
        Hash256 address = TestItem.KeccakA;
        TreePath available = TreePath.FromHexString("1");
        TreePath persistedPath = TreePath.FromHexString("2");
        byte[] availableRlp = FlatTestHelpers.LeafRlp(new byte[40]);
        byte[] persistedRlp = FlatTestHelpers.LeafRlp(new byte[41]);
        byte[] rootRlp = FlatTestHelpers.BranchRlp([null, availableRlp, persistedRlp]);
        using Snapshot snapshot = FlatTestHelpers.MakeSnapshot(_resourcePool, _buffer, content =>
        {
            if (storage)
            {
                content.SetStorageNodeRlp(_buffer, address, TreePath.Empty, rootRlp);
                content.SetStorageNodeRlp(_buffer, address, available, availableRlp);
            }
            else
            {
                content.SetStateNodeRlp(_buffer, TreePath.Empty, rootRlp);
                content.SetStateNodeRlp(_buffer, available, availableRlp);
            }
        });
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(snapshot.From, snapshot.To, data);
        ValueHash256 addressHash = address.ValueHash256;
        bool found = storage ? persisted.TryLoadStorageNodeRlp(addressHash, available, out byte[]? rlp)
            : persisted.TryLoadStateNodeRlp(available, out rlp);
        bool boundary = storage ? persisted.TryLoadStorageNodeRlp(addressHash, persistedPath, out _)
            : persisted.TryLoadStateNodeRlp(persistedPath, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(rlp, Is.EqualTo(availableRlp));
            Assert.That(boundary, Is.False, "a hash-only child is not reconstructed or serialized");
            Assert.DoesNotThrow(() => PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted, dumpWhenFailed: false));
        }
    }

    [Test]
    public void Loader_roundtrips_compacted_final_roots_and_clear_boundaries([Values] bool recreate)
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        byte[] firstState = FlatTestHelpers.LeafRlp(1);
        byte[] finalState = FlatTestHelpers.LeafRlp(2);
        TreePath oldStorage = TreePath.FromHexString("1");
        TreePath newStorage = TreePath.FromHexString("2");
        Hash256 address = TestItem.AddressA.ToAccountPath.ToHash256();
        StateId firstId = new(1, ValueKeccak.Compute(firstState));
        StateId clearId = new(2, ValueKeccak.Compute(finalState));
        StateId finalId = new(3, clearId.StateRoot);
        Snapshot first = _resourcePool.CreateSnapshot(StateId.PreGenesis, firstId, ResourcePool.Usage.MainBlockProcessing, _buffer);
        first.Content.SetStateNodeRlp(_buffer, TreePath.Empty, firstState);
        first.Content.SetStorageNodeRlp(_buffer, address, oldStorage, FlatTestHelpers.LeafRlp(3));
        first.Content.Accounts[TestItem.AddressA] = new Account(1, 10);
        first.Content.Storages[(TestItem.AddressA, (UInt256)1)] = 1;
        Snapshot clear = _resourcePool.CreateSnapshot(firstId, clearId, ResourcePool.Usage.MainBlockProcessing, _buffer);
        clear.Content.SetStateNodeRlp(_buffer, TreePath.Empty, finalState);
        clear.Content.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
        clear.Content.StorageRoots[address] = 0;
        Snapshot final = _resourcePool.CreateSnapshot(clearId, finalId, ResourcePool.Usage.MainBlockProcessing, _buffer);
        final.Content.SetStateNodeRlp(_buffer, TreePath.Empty, finalState);
        if (recreate)
        {
            final.Content.SetStorageNodeRlp(_buffer, address, newStorage, FlatTestHelpers.LeafRlp(4));
            final.Content.Storages[(TestItem.AddressA, (UInt256)2)] = 42;
        }
        using SnapshotPooledList inputs = FlatTestHelpers.SnapshotList(first, clear, final);
        using Snapshot compacted = tier.Resolve<ISnapshotCompactor>().CompactSnapshotBundle(inputs);
        using PersistedSnapshot persisted = tier.ConvertToPersistedBase(compacted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.From, Is.EqualTo(StateId.PreGenesis));
            Assert.That(persisted.To, Is.EqualTo(finalId));
            Assert.That(persisted.TryLoadStateNodeRlp(TreePath.Empty, out byte[]? state), Is.True);
            Assert.That(state, Is.EqualTo(finalState), "the unchanged endpoint publishes its own copy of the state root");
            Assert.That(persisted.TryLoadStorageNodeRlp(address.ValueHash256, oldStorage, out _), Is.False);
            Assert.That(persisted.TryLoadStorageNodeRlp(address.ValueHash256, newStorage, out _), Is.EqualTo(recreate));
            Assert.That(persisted.TryGetSelfDestructFlag(TestItem.AddressA), Is.False);
            Assert.That(persisted.TryGetSlot(TestItem.AddressA, 1, out _), Is.False, "pre-clear flat slots are omitted");
            Assert.DoesNotThrow(() => PersistedSnapshotUtils.ValidatePersistedSnapshot(compacted, persisted, dumpWhenFailed: false));
        }
    }

    private static IEnumerable<TestCaseData> RoundTripTestCases()
    {
        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1000).TestObject;
        })).SetName("Account");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            c.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
        })).SetName("SelfDestruct");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            TreePath path = FlatTestHelpers.CanonicalPath(Keccak.Compute("path"), 4);
            c.SetStateNodeRlp(buffer, path, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x80));
        })).SetName("StateNode_TopPath");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            TreePath path = FlatTestHelpers.CanonicalPath(Keccak.Compute("path"), 8);
            c.SetStateNodeRlp(buffer, path, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x80));
        })).SetName("StateNode_CompactPath");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            TreePath longPath = FlatTestHelpers.CanonicalPath(Keccak.Compute("longpath"), 20);
            c.SetStateNodeRlp(buffer, longPath, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x81));
        })).SetName("StateNode_LongPath");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            byte[] value = new byte[32];
            value[31] = 0xFF;
            c.Storages[(TestItem.AddressA, (UInt256)42)] = new UInt256(value, isBigEndian: true);
        })).SetName("Storage_SingleSlot");

        // Single significant byte < 0x80: RLP wraps it to the byte itself (1 byte), so the
        // stored length is still 1 — distinct from the length-0 absent sentinel.
        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            byte[] value = new byte[32];
            value[31] = 0x05;
            c.Storages[(TestItem.AddressA, (UInt256)9)] = new UInt256(value, isBigEndian: true);
        })).SetName("Storage_SmallSingleByteSlot");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            byte[] value = new byte[32];
            value[31] = 0xAB;
            c.Storages[(TestItem.AddressA, UInt256.Zero)] = new UInt256(value, isBigEndian: true);
        })).SetName("Storage_ZeroSlot");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            c.Storages[(TestItem.AddressA, (UInt256)1)] = null;
            byte[] val = new byte[32];
            val[31] = 0xFF;
            c.Storages[(TestItem.AddressA, (UInt256)2)] = new UInt256(val, isBigEndian: true);
        })).SetName("Storage_NullSlot");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            byte[] val1 = new byte[32]; val1[31] = 0x01;
            byte[] val2 = new byte[32]; val2[31] = 0x02;
            byte[] val3 = new byte[32]; val3[31] = 0x03;
            c.Storages[(TestItem.AddressA, (UInt256)1)] = new UInt256(val1, isBigEndian: true);
            c.Storages[(TestItem.AddressA, (UInt256)2)] = new UInt256(val2, isBigEndian: true);
            c.Storages[(TestItem.AddressB, (UInt256)5)] = new UInt256(val3, isBigEndian: true);
        })).SetName("Storage_MultipleAddresses");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            Hash256 address = Keccak.Compute("address");
            TreePath path = FlatTestHelpers.CanonicalPath(Keccak.Compute("path"), 4);
            c.SetStorageNodeRlp(buffer, address, path, FlatTestHelpers.LeafRlp(0xC1, 0x80));
        })).SetName("StorageNode_TopPath");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            Hash256 address = Keccak.Compute("address");
            TreePath path = FlatTestHelpers.CanonicalPath(Keccak.Compute("path"), 6);
            c.SetStorageNodeRlp(buffer, address, path, FlatTestHelpers.LeafRlp(0xC1, 0x80));
        })).SetName("StorageNode_CompactPath");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            Hash256 address = Keccak.Compute("address");
            TreePath longPath = FlatTestHelpers.CanonicalPath(Keccak.Compute("longpath"), 18);
            c.SetStorageNodeRlp(buffer, address, longPath, FlatTestHelpers.LeafRlp(0xC3, 0x80, 0x81, 0x82));
        })).SetName("StorageNode_LongPath");

        yield return new TestCaseData((Action<SnapshotContent, INodeRecordBuffer>)((c, buffer) =>
        {
            c.Accounts[TestItem.AddressA] = Build.An.Account
                .WithBalance(12345).WithNonce(7).TestObject;
            c.Accounts[TestItem.AddressB] = Build.An.Account
                .WithBalance(0).WithNonce(0)
                .WithCode([0x60, 0x00])
                .WithStorageRoot(Keccak.Compute("storage")).TestObject;
            c.Accounts[TestItem.AddressC] = null;

            byte[] slotVal1 = new byte[32]; slotVal1[31] = 0xFF;
            byte[] slotVal2 = new byte[32]; slotVal2[0] = 0x01; slotVal2[31] = 0x02;
            c.Storages[(TestItem.AddressA, (UInt256)1)] = new UInt256(slotVal1, isBigEndian: true);
            c.Storages[(TestItem.AddressA, (UInt256)2)] = new UInt256(slotVal2, isBigEndian: true);
            c.Storages[(TestItem.AddressB, (UInt256)42)] = null;

            c.SelfDestructedStorageAddresses[TestItem.AddressD] = false;
            c.SelfDestructedStorageAddresses[TestItem.AddressE] = true;

            TreePath topStatePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("tp"), 3);
            c.SetStateNodeRlp(buffer, topStatePath, FlatTestHelpers.LeafRlp(0xC1, 0x80));

            TreePath shortStatePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("sp"), 8);
            c.SetStateNodeRlp(buffer, shortStatePath, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x80));

            TreePath longStatePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("lp"), 20);
            c.SetStateNodeRlp(buffer, longStatePath, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x81));

            Hash256 storageAddr = Keccak.Compute("storageAddr");
            TreePath topStoragePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("tsp"), 3);
            c.SetStorageNodeRlp(buffer, storageAddr, topStoragePath, FlatTestHelpers.LeafRlp(0xC1, 0x80));

            TreePath shortStoragePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("ssp"), 6);
            c.SetStorageNodeRlp(buffer, storageAddr, shortStoragePath, FlatTestHelpers.LeafRlp(0xC1, 0x80));

            TreePath longStoragePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("lsp"), 18);
            c.SetStorageNodeRlp(buffer, storageAddr, longStoragePath, FlatTestHelpers.LeafRlp(0xC3, 0x80, 0x81, 0x82));
        })).SetName("AllDataTypes");
    }

    [TestCaseSource(nameof(RoundTripTestCases))]
    public void RoundTrip(Action<SnapshotContent, INodeRecordBuffer> populateContent)
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("1"));

        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        populateContent(content, _buffer);

        using Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        Assert.DoesNotThrow(() => PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted));
    }

    // Covers the scanner slot-decode path (PersistedSnapshotScanner.SlotEntry.Value), which
    // PersistPersistedSnapshot uses to flush slots back into the flat DB. Slot values are now
    // RLP-wrapped; this asserts varied widths (1-byte < 0x80, 1-byte >= 0x80, full 32 bytes)
    // decode correctly and that a null/deleted slot is surfaced as null (length-0 sentinel).
    [Test]
    public void Slot_scanner_round_trips_rlp_wrapped_values()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("scan"));

        byte[] small = new byte[32]; small[31] = 0x05;      // RLP(0x05) = 0x05
        byte[] high = new byte[32]; high[31] = 0xFF;         // RLP(0xff) = 0x81 0xff
        byte[] full = new byte[32];
        for (int i = 0; i < 32; i++) full[i] = (byte)(i + 1); // RLP = 0xa0 + 32 bytes

        SnapshotContent content = new();
        content.Storages[(TestItem.AddressA, (UInt256)1)] = new UInt256(small, isBigEndian: true);
        content.Storages[(TestItem.AddressA, (UInt256)2)] = new UInt256(high, isBigEndian: true);
        content.Storages[(TestItem.AddressA, (UInt256)3)] = null;
        content.Storages[(TestItem.AddressB, (UInt256)4)] = new UInt256(full, isBigEndian: true);
        content.Storages[(TestItem.AddressB, (UInt256)5)] = UInt256.Zero;

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        Dictionary<(Address, UInt256), UInt256?> scanned = [];
        using (WholeReadSession session = persisted.BeginWholeReadSession())
        {
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, persisted);
            foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
                foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
                    scanned[(entry.Address, slot.Slot)] = slot.Value;
        }

        foreach (KeyValuePair<(Address, UInt256), UInt256?> entry in scanned)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(persisted.TryGetSlot(entry.Key.Item1, entry.Key.Item2, out UInt256? value), Is.True);
                Assert.That(value, Is.EqualTo(entry.Value));
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scanned[(TestItem.AddressA, (UInt256)1)]!.Value.ToBigEndian(), Is.EqualTo(small));
            Assert.That(scanned[(TestItem.AddressA, (UInt256)2)]!.Value.ToBigEndian(), Is.EqualTo(high));
            Assert.That(scanned[(TestItem.AddressA, (UInt256)3)], Is.Null, "deleted slot must surface as null");
            Assert.That(scanned[(TestItem.AddressB, (UInt256)4)]!.Value.ToBigEndian(), Is.EqualTo(full));
            Assert.That(scanned[(TestItem.AddressB, (UInt256)5)], Is.EqualTo((UInt256?)UInt256.Zero), "zero must remain distinct from a deleted slot");
        }
    }

    // Drives the scanner across every entry kind in one pass: normal vs deleted account,
    // self-destruct false (0x00) vs true (0x01), present vs deleted slot, and state/storage
    // trie nodes spread across all three depth tiers (top/compact/fallback).
    [Test]
    public void FullScan_DecodesAccounts_SelfDestruct_Slots_StateAndStorageNodes()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("fullscan"));

        byte[] slotVal = new byte[32]; slotVal[31] = 0x11;

        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1000).WithNonce(3).TestObject;
        content.Accounts[TestItem.AddressC] = null;                          // deleted marker
        content.Storages[(TestItem.AddressA, (UInt256)1)] = new UInt256(slotVal, isBigEndian: true);
        content.Storages[(TestItem.AddressA, (UInt256)2)] = null;
        content.SelfDestructedStorageAddresses[TestItem.AddressD] = false;   // 0x00 destructed
        content.SelfDestructedStorageAddresses[TestItem.AddressE] = true;    // 0x01 new-account
        TreePath stTop = FlatTestHelpers.CanonicalPath(Keccak.Compute("st-top"), 3);
        TreePath stMid = FlatTestHelpers.CanonicalPath(Keccak.Compute("st-mid"), 8);
        TreePath stLong = FlatTestHelpers.CanonicalPath(Keccak.Compute("st-long"), 20);
        content.SetStateNodeRlp(_buffer, stTop, FlatTestHelpers.LeafRlp(0xC1, 0x80));
        content.SetStateNodeRlp(_buffer, stMid, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x80));
        content.SetStateNodeRlp(_buffer, stLong, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x81));
        Hash256 storageAddr = Keccak.Compute("storage-addr");
        TreePath snTop = FlatTestHelpers.CanonicalPath(Keccak.Compute("sn-top"), 3);
        TreePath snMid = FlatTestHelpers.CanonicalPath(Keccak.Compute("sn-mid"), 6);
        TreePath snLong = FlatTestHelpers.CanonicalPath(Keccak.Compute("sn-long"), 18);
        content.SetStorageNodeRlp(_buffer, storageAddr, snTop, FlatTestHelpers.LeafRlp(0xC1, 0x81));
        content.SetStorageNodeRlp(_buffer, storageAddr, snMid, FlatTestHelpers.LeafRlp(0xC1, 0x82));
        content.SetStorageNodeRlp(_buffer, storageAddr, snLong, FlatTestHelpers.LeafRlp(0xC3, 0x80, 0x81, 0x82));

        using Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        Dictionary<Address, (bool HasAccount, UInt256? Balance, bool? Sd)> perAddr = [];
        Dictionary<(Address, UInt256), UInt256?> slots = [];
        int stateNodes = 0, storageNodes = 0;

        using (WholeReadSession session = persisted.BeginWholeReadSession())
        {
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, persisted);
            foreach (WholeReadScanner.PerAddressEntry e in scanner.PerAddresses)
            {
                perAddr[e.Address] = (e.HasAccount, e.Account?.Balance, e.SelfDestructFlag);
                foreach (WholeReadScanner.SlotEntry s in e.Slots)
                    slots[(e.Address, s.Slot)] = s.Value;
            }
            foreach (WholeReadScanner.StateNodeEntry n in scanner.StateNodes)
            {
                _ = n.Path;          // exercise the stage-specific path decode
                Assert.That(n.Rlp.Length, Is.GreaterThan(0));
                stateNodes++;
            }
            foreach (WholeReadScanner.StorageNodeEntry n in scanner.StorageNodes)
            {
                _ = n.Path;
                _ = n.AddressHash;
                Assert.That(n.Rlp.Length, Is.GreaterThan(0));
                storageNodes++;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(perAddr[TestItem.AddressA].HasAccount, Is.True);
            Assert.That(perAddr[TestItem.AddressA].Balance, Is.EqualTo((UInt256)1000));
            Assert.That(perAddr[TestItem.AddressA].Sd, Is.Null, "address with no self-destruct sub-tag → null flag");
            Assert.That(perAddr[TestItem.AddressC].HasAccount, Is.True, "deleted account still has a (marker) sub-tag");
            Assert.That(perAddr[TestItem.AddressC].Balance, Is.Null, "deleted account decodes to null");
            Assert.That(perAddr[TestItem.AddressD].HasAccount, Is.False, "self-destruct-only address has no account sub-tag");
            Assert.That(perAddr[TestItem.AddressD].Sd, Is.False, "0x00 marker → destructed");
            Assert.That(perAddr[TestItem.AddressE].Sd, Is.True, "0x01 marker → new account");

            Assert.That(slots[(TestItem.AddressA, (UInt256)1)]!.Value.ToBigEndian(), Is.EqualTo(slotVal));
            Assert.That(slots[(TestItem.AddressA, (UInt256)2)], Is.Null, "deleted slot surfaces as null");

            Assert.That(stateNodes, Is.EqualTo(snapshot.StateNodes.Count()), "all reachable state nodes, including connecting ancestors");
            Assert.That(storageNodes, Is.EqualTo(snapshot.StorageNodes.Count()), "all reachable storage nodes, including connecting ancestors");
        }
    }

    // A single account with enough storage slots to span many 4 KiB data blocks (and far exceed the
    // scanner's 32-record linear skip) must round-trip through the streaming scanner: the per-address pass
    // reads the account at the group head, then the Slots view streams every slot off the shared cursor
    // exactly once. A second address after it verifies the scanner advances cleanly to the next group.
    [Test]
    public void Scanner_StreamsLargeSlotAccountAcrossBlocks()
    {
        const int slotCount = 3000;
        byte[] bigBytes = new byte[20]; bigBytes[0] = 0x11;
        byte[] nextBytes = new byte[20]; nextBytes[0] = 0x22;
        Address big = new(bigBytes);
        Address next = new(nextBytes);

        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("large-slots"));

        SnapshotContent content = new();
        content.Accounts[big] = Build.An.Account.WithBalance(1000).WithNonce(7).TestObject;
        byte[][] expected = new byte[slotCount + 1][];
        for (int i = 1; i <= slotCount; i++)
        {
            byte[] v = new byte[32];
            v[30] = (byte)((i >> 8) & 0xFF);
            v[31] = (byte)(i & 0xFF);
            expected[i] = v;
            content.Storages[(big, (UInt256)i)] = new UInt256(v, isBigEndian: true);
        }
        content.Accounts[next] = Build.An.Account.WithBalance(2000).TestObject;
        byte[] nextSlot = new byte[32]; nextSlot[31] = 0x2A;
        content.Storages[(next, (UInt256)42)] = new UInt256(nextSlot, isBigEndian: true);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        Dictionary<Address, (bool HasAccount, UInt256? Balance)> accounts = [];
        Dictionary<(Address, UInt256), byte[]?> slots = [];
        using (WholeReadSession session = persisted.BeginWholeReadSession())
        {
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, persisted);
            foreach (WholeReadScanner.PerAddressEntry e in scanner.PerAddresses)
            {
                accounts[e.Address] = (e.HasAccount, e.Account?.Balance);
                foreach (WholeReadScanner.SlotEntry s in e.Slots)
                    slots[(e.Address, s.Slot)] = s.Value?.ToBigEndian();
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts[big].HasAccount, Is.True);
            Assert.That(accounts[big].Balance, Is.EqualTo((UInt256)1000));
            Assert.That(accounts[next].HasAccount, Is.True);
            Assert.That(accounts[next].Balance, Is.EqualTo((UInt256)2000));
            Assert.That(slots.Count, Is.EqualTo(slotCount + 1), "every slot of both addresses streamed exactly once");
        }
        for (int i = 1; i <= slotCount; i++)
            Assert.That(slots[(big, (UInt256)i)], Is.EqualTo(expected[i]), $"slot {i}");
        Assert.That(slots[(next, (UInt256)42)], Is.EqualTo(nextSlot));
    }

    // Reading a per-address entry without draining its Slots must not strand the shared cursor mid-slots:
    // the per-address enumerator has to skip the undrained tail to reach the next group. With more slots
    // than the 32-record linear budget, this exercises the binary-search (Seek) skip past the address.
    [Test]
    public void Scanner_SkipsUndrainedSlots_ToReachNextGroup()
    {
        const int slotCount = 200; // > the 32-record linear skip, so the skip must Seek past the address
        byte[] bigBytes = new byte[20]; bigBytes[0] = 0x11;
        byte[] nextBytes = new byte[20]; nextBytes[0] = 0x22;
        Address big = new(bigBytes);
        Address next = new(nextBytes);

        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("undrained-slots"));

        SnapshotContent content = new();
        content.Accounts[big] = Build.An.Account.WithBalance(1000).WithNonce(7).TestObject;
        for (int i = 1; i <= slotCount; i++)
        {
            byte[] v = new byte[32];
            v[30] = (byte)((i >> 8) & 0xFF);
            v[31] = (byte)(i & 0xFF);
            content.Storages[(big, (UInt256)i)] = new UInt256(v, isBigEndian: true);
        }
        content.Accounts[next] = Build.An.Account.WithBalance(2000).TestObject;
        byte[] nextSlotValue = new byte[32]; nextSlotValue[31] = 0x2A;
        content.Storages[(next, (UInt256)42)] = new UInt256(nextSlotValue, isBigEndian: true);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        (bool HasAccount, UInt256? Balance)? bigEntry = null;
        (bool HasAccount, UInt256? Balance)? nextEntry = null;
        Dictionary<(Address, UInt256), byte[]?> nextSlots = [];
        using (WholeReadSession session = persisted.BeginWholeReadSession())
        {
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, persisted);
            foreach (WholeReadScanner.PerAddressEntry e in scanner.PerAddresses)
            {
                if (e.Address == big)
                {
                    // Read the account but deliberately leave big's Slots undrained.
                    bigEntry = (e.HasAccount, e.Account?.Balance);
                }
                else
                {
                    nextEntry = (e.HasAccount, e.Account?.Balance);
                    foreach (WholeReadScanner.SlotEntry s in e.Slots)
                        nextSlots[(e.Address, s.Slot)] = s.Value?.ToBigEndian();
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bigEntry?.HasAccount, Is.True);
            Assert.That(bigEntry?.Balance, Is.EqualTo((UInt256)1000));
            Assert.That(nextEntry?.HasAccount, Is.True, "the next group is reached after skipping big's undrained slots");
            Assert.That(nextEntry?.Balance, Is.EqualTo((UInt256)2000));
            Assert.That(nextSlots[(next, (UInt256)42)], Is.EqualTo(nextSlotValue));
        }
    }

    // When a column / sub-tag tier is absent, the enumerators must seek past it gracefully:
    // state nodes only in the top tier, storage nodes in top/fallback but not compact, and no
    // per-address column at all.
    [Test]
    public void Scan_AbsentTiers_SkipMissingColumnsAndSubTags()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("absent"));

        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        TreePath onlyTop = FlatTestHelpers.CanonicalPath(Keccak.Compute("only-top"), 3);
        content.SetStateNodeRlp(_buffer, onlyTop, FlatTestHelpers.LeafRlp(0xC1, 0x80));
        Hash256 storageAddr = Keccak.Compute("absent-storage");
        TreePath onlyFallback = FlatTestHelpers.CanonicalPath(Keccak.Compute("only-fallback"), 18);
        content.SetStorageNodeRlp(_buffer, storageAddr, onlyFallback, FlatTestHelpers.LeafRlp(0xC3, 0x80, 0x81, 0x82));

        using Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        int perAddrCount = 0, stateNodes = 0, storageNodes = 0;
        using (WholeReadSession session = persisted.BeginWholeReadSession())
        {
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, persisted);
            foreach (WholeReadScanner.PerAddressEntry _ in scanner.PerAddresses) perAddrCount++;
            foreach (WholeReadScanner.StateNodeEntry n in scanner.StateNodes) { _ = n.Path; stateNodes++; }
            foreach (WholeReadScanner.StorageNodeEntry n in scanner.StorageNodes) { _ = n.Path; storageNodes++; }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(perAddrCount, Is.EqualTo(0), "no per-address column → empty enumeration");
            Assert.That(stateNodes, Is.EqualTo(2), "root and leaf in the top tier, compact/fallback columns absent");
            Assert.That(storageNodes, Is.EqualTo(2), "top-tier root and fallback-tier leaf, compact sub-tag absent");
        }
    }

    // Exercises the read-path miss branches: a present snapshot queried for keys that are
    // absent at every level — unknown address, present-address/absent-slot, present-address/
    // no-self-destruct, absent state node, absent storage addressHash, and present-addressHash/
    // absent-path (same and different sub-tag tier).
    [Test]
    public void Queries_ForAbsentKeys_ReturnMisses()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("miss"));

        byte[] slotVal = new byte[32]; slotVal[31] = 0x07;
        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(5).TestObject;
        content.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(9).TestObject; // 2nd address → real address BTree
        content.Storages[(TestItem.AddressA, (UInt256)1)] = new UInt256(slotVal, isBigEndian: true);
        content.SelfDestructedStorageAddresses[TestItem.AddressA] = true;
        TreePath statePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("sp"), 4);
        content.SetStateNodeRlp(_buffer, statePath, FlatTestHelpers.LeafRlp(0xC1, 0x80));
        Hash256 storageHashObj = Keccak.Compute("sh");
        TreePath storagePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("stp"), 4);
        content.SetStorageNodeRlp(_buffer, storageHashObj, storagePath, FlatTestHelpers.LeafRlp(0xC1, 0x81));

        using Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        // Unknown address: BTree seek misses.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.TryGetAccount(TestItem.AddressB, out Account? accB), Is.False);
            Assert.That(accB, Is.Null);
            Assert.That(persisted.TryGetSlot(TestItem.AddressB, (UInt256)1, out UInt256? sv), Is.False);
            Assert.That(persisted.TryGetSelfDestructFlag(TestItem.AddressB), Is.Null);

            // Present address, absent slot index; present address with no slot/self-destruct sub-tag.
            Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)999, out sv), Is.False);
            Assert.That(persisted.TryGetSlot(TestItem.AddressC, (UInt256)1, out sv), Is.False);
            Assert.That(persisted.TryGetSelfDestructFlag(TestItem.AddressC), Is.Null);

            // Absent state node.
            Assert.That(persisted.TryLoadStateNodeRlp(FlatTestHelpers.CanonicalPath(Keccak.Compute("absent"), 4), out byte[]? sn), Is.False);
            Assert.That(sn, Is.Null);
        }

        // Storage node: absent addressHash; present addressHash with absent path in the same
        // sub-tag tier and in a different (absent) tier.
        ValueHash256 storageHash = new(storageHashObj.Bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.TryLoadStorageNodeRlp(new ValueHash256(Keccak.Compute("nope").Bytes), storagePath, out _), Is.False);
            Assert.That(persisted.TryLoadStorageNodeRlp(storageHash, FlatTestHelpers.CanonicalPath(Keccak.Compute("absentSameTier"), 4), out _), Is.False);
            Assert.That(persisted.TryLoadStorageNodeRlp(storageHash, FlatTestHelpers.CanonicalPath(Keccak.Compute("absentDeep"), 18), out _), Is.False);

            Assert.That(persisted.TryGetAccount(TestItem.AddressA, out _), Is.True);
            Assert.That(persisted.TryLoadStorageNodeRlp(storageHash, storagePath, out _), Is.True);
        }
    }

    // An empty snapshot has no address column (cached BTree bound is empty) and no node
    // columns, so every read returns a miss without faulting.
    [Test]
    public void Queries_OnEmptySnapshot_ReturnMisses()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("empty-reads"));
        Snapshot snapshot = new(from, to, new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.TryGetAccount(TestItem.AddressA, out _), Is.False);
            Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)1, out _), Is.False);
            Assert.That(persisted.TryGetSelfDestructFlag(TestItem.AddressA), Is.Null);
            Assert.That(persisted.TryLoadStateNodeRlp(FlatTestHelpers.CanonicalPath(Keccak.Compute("p"), 4), out _), Is.False);
            Assert.That(persisted.TryLoadStorageNodeRlp(new ValueHash256(Keccak.Compute("h").Bytes), FlatTestHelpers.CanonicalPath(Keccak.Compute("p"), 4), out _), Is.False);
        }

        // Build-based snapshots carry no blob_range metadata → BlobRange.None → advise is a no-op.
        Assert.DoesNotThrow(() => persisted.AdviseWillNeedBlobRange());
        Assert.DoesNotThrow(() => persisted.AdviseDontNeedBlobRange());
    }

    // Drives PersistedSnapshotStack's newest-first probe loops over a two-snapshot stack:
    // hits in the newer and (after a newer miss) the older snapshot, full misses, the
    // self-destruct slot boundary, and the detailed-metrics observations.
    [Test]
    public void Stack_ProbesNewestFirst_AcrossAllKinds()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("st1"));
        StateId s2 = new(2, Keccak.Compute("st2"));

        byte[] v1 = new byte[32]; v1[31] = 0x11;
        byte[] v2 = new byte[32]; v2[31] = 0x22;

        INodeRecordBufferPin olderBufferRetain = _buffer.PinHead();
        SnapshotContent older = new();
        older.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
        older.Accounts[TestItem.AddressD] = Build.An.Account.WithBalance(40).TestObject;
        older.Storages[(TestItem.AddressA, (UInt256)1)] = new UInt256(v1, isBigEndian: true);
        older.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
        TreePath statePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("st-p"), 4);
        older.SetStateNodeRlp(_buffer, statePath, FlatTestHelpers.LeafRlp(0xC1, 0x80));
        Hash256 storageHashObj = Keccak.Compute("st-sh");
        TreePath storagePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("st-sp"), 4);
        older.SetStorageNodeRlp(_buffer, storageHashObj, storagePath, FlatTestHelpers.LeafRlp(0xC1, 0x81));

        SnapshotContent newer = new();
        newer.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(200).TestObject;
        newer.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(7).TestObject;
        newer.Storages[(TestItem.AddressA, (UInt256)2)] = new UInt256(v2, isBigEndian: true);

        using Snapshot olderSnapshot = new(s0, s1, older, _resourcePool, ResourcePool.Usage.MainBlockProcessing, olderBufferRetain);
        byte[] olderData = PersistedSnapshotBuilderTestExtensions.Build(olderSnapshot, _blobs);
        byte[] newerData = PersistedSnapshotBuilderTestExtensions.Build(
            new Snapshot(s1, s2, newer, _resourcePool, ResourcePool.Usage.MainBlockProcessing, null), _blobs);

        PersistedSnapshotList list = new(2) { CreatePersistedSnapshot(s0, s1, olderData), CreatePersistedSnapshot(s1, s2, newerData) };
        using PersistedSnapshotStack stack = new(list, recordDetailedMetrics: true);

        // Account: newest wins; older-only address resolves after the newer miss; full miss.
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(stack.TryGetAccount(TestItem.AddressA, out Account? a), Is.True);
#pragma warning restore NUnit2045
        Assert.That(a!.Balance, Is.EqualTo((UInt256)200), "newest snapshot wins");
        Assert.That(stack.TryGetAccount(TestItem.AddressD, out Account? d), Is.True);
        Assert.That(d!.Balance, Is.EqualTo((UInt256)40), "older-only address resolves after newer miss");
        Assert.That(stack.TryGetAccount(TestItem.AddressF, out _), Is.False);

        // Self-destruct: only the older snapshot carries it.
        Assert.That(stack.TryGetSelfDestruct(TestItem.AddressA, out int sdIdx), Is.True);
        Assert.That(sdIdx, Is.EqualTo(0));
        Assert.That(stack.TryGetSelfDestruct(TestItem.AddressF, out _), Is.False);

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // Slot: newer holds slot 2, older holds slot 1; both resolve.
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(stack.TryGetSlot(TestItem.AddressA, (UInt256)2, -1, start, out UInt256? sv2), Is.True);
#pragma warning restore NUnit2045
        Assert.That(sv2!.Value.ToBigEndian().AsSpan()[^1], Is.EqualTo((byte)0x22));
        Assert.That(stack.TryGetSlot(TestItem.AddressA, (UInt256)1, -1, start, out UInt256? sv1), Is.True);
        Assert.That(sv1!.Value.ToBigEndian().AsSpan()[^1], Is.EqualTo((byte)0x11));
        // Slot below the self-destruct boundary resolves to null (storage wiped).
        Assert.That(stack.TryGetSlot(TestItem.AddressA, (UInt256)999, 0, start, out UInt256? svNull), Is.True);
        Assert.That(svNull, Is.Null);
        // Slot fully absent (no boundary) falls through.
        Assert.That(stack.TryGetSlot(TestItem.AddressF, (UInt256)1, -1, start, out _), Is.False);

        // State / storage node RLP: present (in older) and absent.
        Assert.That(stack.TryLoadStateRlp(statePath, out byte[]? srlp), Is.True);
        Assert.That(srlp, Is.Not.Null);
        Assert.That(stack.TryLoadStateRlp(FlatTestHelpers.CanonicalPath(Keccak.Compute("nope-st"), 4), out _), Is.False);
        Assert.That(stack.TryLoadStorageRlp(storageHashObj, storagePath, out byte[]? strlp), Is.True);
        Assert.That(strlp, Is.Not.Null);
        Assert.That(stack.TryLoadStorageRlp(storageHashObj, FlatTestHelpers.CanonicalPath(Keccak.Compute("nope-sp"), 4), out _), Is.False);
    }

    [Test]
    public void ActivePersistedSnapshotCount_TracksConstructionAndDisposal()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to1 = new(1, Keccak.Compute("one"));
        StateId to2 = new(2, Keccak.Compute("two"));

        Snapshot inMem1 = new(from, to1, new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        Snapshot inMem2 = new(from, to2, new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(inMem1, _blobs);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(inMem2, _blobs);

        long baseline = Active();

        PersistedSnapshot s1 = CreatePersistedSnapshot(from, to1, data1);
        PersistedSnapshot s2 = CreatePersistedSnapshot(from, to2, data2);

        Assert.That(Active(), Is.EqualTo(baseline + 2));

        s1.Dispose();
        Assert.That(Active(), Is.EqualTo(baseline + 1));

        s2.Dispose();
        Assert.That(Active(), Is.EqualTo(baseline));

        static long Active()
        {
            long total = 0;
            foreach (KeyValuePair<PersistedSnapshotLabel, long> kv in Metrics.ActivePersistedSnapshotCount)
                total += kv.Value;
            return total;
        }
    }

    [Test]
    public void BlobArena_FrontierResets_WhenLastPersistedSnapshotDisposes()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("reset"));

        using Snapshot inMem = new(from, to, new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing, _buffer.PinHead());
        TreePath path = FlatTestHelpers.CanonicalPath(Keccak.Compute("p"), 8);
        inMem.Content.SetStateNodeRlp(_buffer, path, FlatTestHelpers.LeafRlp(0xC2, 0x80, 0x80));

        long baselineBytes = Metrics.BlobAllocatedBytes;
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(inMem, _blobs);
        long afterBuild = Metrics.BlobAllocatedBytes;
        Assert.That(afterBuild, Is.GreaterThan(baselineBytes), "Building a snapshot with trie nodes should grow blob-allocated bytes");

        TestFixtureHelpers.CreatePersistedSnapshot(_memArena, _blobs, from, to, data)
            .Dispose();

        // After the last external lease drops, the manager's TryResetOrphanedFrontier
        // should have reset the file's frontier and pushed the delta back to the gauge.
        Assert.That(Metrics.BlobAllocatedBytes, Is.EqualTo(baselineBytes),
            "Blob-allocated bytes must drop back to baseline once the last referencing snapshot is disposed");
    }

    [TestCase((ushort)0, 0)]
    [TestCase((ushort)42, 12345)]
    [TestCase(ushort.MaxValue, int.MaxValue)]
    public void NodeRef_ReadWrite_RoundTrip(ushort id, int offset)
    {
        Assert.That(NodeRef.Size, Is.EqualTo(6));
        NodeRef original = new(id, offset);
        byte[] buffer = new byte[NodeRef.Size];
        NodeRef.Write(buffer, original);
        NodeRef decoded = NodeRef.Read(buffer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.BlobArenaId, Is.EqualTo(id));
            Assert.That(decoded.RlpDataOffset, Is.EqualTo(offset));
        }
    }

    [Test]
    public void PersistedSnapshotList_Queries_NewestFirst()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        // path length 4 → StateTopNodes column
        TreePath path = FlatTestHelpers.CanonicalPath(Keccak.Compute("path"), 4);
        byte[] rlp1 = FlatTestHelpers.LeafRlp(0xC0);
        byte[] rlp2 = FlatTestHelpers.LeafRlp(0xC1, 0x80);

        INodeRecordBufferPin bufferRetain1 = _buffer.PinHead();
        SnapshotContent content1 = new();
        content1.SetStateNodeRlp(_buffer, path, rlp1);
        using Snapshot snap1 = new(s0, s1, content1, _resourcePool, ResourcePool.Usage.MainBlockProcessing, bufferRetain1);

        INodeRecordBufferPin bufferRetain2 = _buffer.PinHead();
        SnapshotContent content2 = new();
        content2.SetStateNodeRlp(_buffer, path, rlp2);
        using Snapshot snap2 = new(s1, s2, content2, _resourcePool, ResourcePool.Usage.MainBlockProcessing, bufferRetain2);

        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1, _blobs);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2, _blobs);

        PersistedSnapshot p1 = CreatePersistedSnapshot(s0, s1, data1);
        PersistedSnapshot p2 = CreatePersistedSnapshot(s1, s2, data2);

        // Ordered oldest-first; query newest-first via indexer
        PersistedSnapshotList list = new(2) { p1, p2 };
        byte[]? result = null;
        bool found = false;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].TryLoadStateNodeRlp(path, out result))
            {
                found = true;
                break;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(result, Is.EqualTo(rlp2));
        }
    }

    [Test]
    public void Storage_NestedMerge_OverlappingAddresses()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        Address addrA = TestItem.AddressA;
        Address addrB = TestItem.AddressB;
        byte[] val1 = new byte[32]; val1[31] = 0x01;
        byte[] val2 = new byte[32]; val2[31] = 0x02;
        byte[] val3 = new byte[32]; val3[31] = 0x03;

        SnapshotContent content1 = new();
        content1.Storages[(addrA, (UInt256)1)] = new UInt256(val1, isBigEndian: true);
        content1.Storages[(addrB, (UInt256)5)] = new UInt256(val2, isBigEndian: true);
        Snapshot snap1 = new(s0, s1, content1, _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1, _blobs);

        SnapshotContent content2 = new();
        content2.Storages[(addrA, (UInt256)1)] = new UInt256(val3, isBigEndian: true);
        content2.Storages[(addrA, (UInt256)2)] = new UInt256(val2, isBigEndian: true);
        Snapshot snap2 = new(s1, s2, content2, _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2, _blobs);

        PersistedSnapshotList toMerge = new(2) { CreatePersistedSnapshot(s0, s1, data1), CreatePersistedSnapshot(s1, s2, data2) };
        byte[] merged = PersistedSnapshotBuilderTestExtensions.NWayMergeSnapshots(toMerge);
        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s2, merged);

#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(persisted.TryGetSlot(addrA, (UInt256)1, out UInt256? slot1), Is.True);
#pragma warning restore NUnit2045
        Assert.That(slot1.GetValueOrDefault().ToMinimalBigEndian()[0], Is.EqualTo(0x03));

        Assert.That(persisted.TryGetSlot(addrA, (UInt256)2, out UInt256? slot2), Is.True);
        Assert.That(slot2.GetValueOrDefault().ToMinimalBigEndian()[0], Is.EqualTo(0x02));

        Assert.That(persisted.TryGetSlot(addrB, (UInt256)5, out UInt256? slot5), Is.True);
        Assert.That(slot5.GetValueOrDefault().ToMinimalBigEndian()[0], Is.EqualTo(0x02));
    }

    private static IEnumerable<TestCaseData> NullSlotMergeCases()
    {
        byte[] nonZero = new byte[32];
        nonZero[31] = 0xFF;

        yield return new TestCaseData(
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = new UInt256(nonZero, isBigEndian: true)),
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = null),
            (Action<PersistedSnapshot>)(persisted =>
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)1, out UInt256? slot), Is.True);
                    Assert.That(slot, Is.Null, "Null slot should override value after merge");
                }
            })).SetName("NullOverridesValue");

        yield return new TestCaseData(
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = new UInt256(7)),
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = UInt256.Zero),
            (Action<PersistedSnapshot>)(persisted =>
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)1, out UInt256? slot), Is.True);
                    Assert.That(slot, Is.EqualTo(UInt256.Zero));
                }
            })).SetName("ExplicitZeroOverridesValue");

        yield return new TestCaseData(
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = null),
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = new UInt256(nonZero, isBigEndian: true)),
            (Action<PersistedSnapshot>)(persisted =>
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)1, out UInt256? slot), Is.True);
                    Assert.That(slot, Is.EqualTo(new UInt256(nonZero, isBigEndian: true)), "Value should override null slot after merge");
                }
            })).SetName("ValueOverridesNull");

        yield return new TestCaseData(
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = null),
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)2)] = new UInt256(nonZero, isBigEndian: true)),
            (Action<PersistedSnapshot>)(persisted =>
            {
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
                Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)1, out UInt256? slot1), Is.True);
#pragma warning restore NUnit2045
                Assert.That(slot1, Is.Null, "Null slot from older should be preserved");

                Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)2, out UInt256? slot2), Is.True);
                Assert.That(slot2.GetValueOrDefault().ToBigEndian().AsSpan().IndexOfAnyExcept((byte)0), Is.GreaterThanOrEqualTo(0), "Value from newer should be present");
            })).SetName("NullPreservedAndValueCarried");
    }

    [TestCaseSource(nameof(NullSlotMergeCases))]
    public void Storage_NullSlot_Merge(
        Action<SnapshotContent> populateOlder,
        Action<SnapshotContent> populateNewer,
        Action<PersistedSnapshot> verify)
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        SnapshotContent olderContent = new();
        populateOlder(olderContent);
        Snapshot older = new(s0, s1, olderContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        byte[] dataOlder = PersistedSnapshotBuilderTestExtensions.Build(older, _blobs);

        SnapshotContent newerContent = new();
        populateNewer(newerContent);
        Snapshot newer = new(s1, s2, newerContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing, null);
        byte[] dataNewer = PersistedSnapshotBuilderTestExtensions.Build(newer, _blobs);

        PersistedSnapshotList toMerge = new(2) { CreatePersistedSnapshot(s0, s1, dataOlder), CreatePersistedSnapshot(s1, s2, dataNewer) };
        byte[] merged = PersistedSnapshotBuilderTestExtensions.NWayMergeSnapshots(toMerge);
        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s2, merged);

        verify(persisted);
    }

    // Round-trips account / self-destruct / slot / storage-node across a range of slot counts,
    // including a multi-page snapshot, then re-reads after AdviseDontNeed drops the kernel pages.
    [Test]
    public void RoundTrips_AcrossSlotCounts([Values(4, 400, 4000)] int slotCount)
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("warmup"));

        Address addr = TestItem.AddressA;
        Hash256 addrHashKey = new(addr.ToAccountPath.Bytes);
        Account expectedAccount = Build.An.Account.WithBalance(987654321).WithNonce(11).TestObject;
        TreePath storagePath = FlatTestHelpers.CanonicalPath(Keccak.Compute("warmup-spath"), 6);
        byte[] storageNodeRlp = FlatTestHelpers.LeafRlp(0xC3, 0x80, 0x81, 0x82);

        INodeRecordBufferPin bufferPin = _buffer.PinHead();
        SnapshotContent content = new();
        content.Accounts[addr] = expectedAccount;
        content.SelfDestructedStorageAddresses[addr] = true;
        content.SetStorageNodeRlp(_buffer, addrHashKey, storagePath, storageNodeRlp);
        for (int i = 0; i < slotCount; i++)
        {
            byte[] val = new byte[32];
            BinaryPrimitives.WriteInt32BigEndian(val.AsSpan(28, 4), i + 1);
            content.Storages[(addr, (UInt256)i + 1)] = new UInt256(val, isBigEndian: true);
        }

        using Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing, bufferPin);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        // The flat sorted table materialises a full record per slot, so a large slot count exceeds
        // the shared 64 KiB fixture arena — use a roomier local arena for this case.
        string arenaDir = Path.Combine(Path.GetTempPath(), $"nm-pstest-rt-{Guid.NewGuid():N}");
        using ArenaManager arena = TestFixtureHelpers.CreateArenaManager(arenaDir, 64 * 1024 * 1024);
        using PersistedSnapshot persisted = TestFixtureHelpers.CreatePersistedSnapshot(arena, _blobs, from, to, data);

        // Per-address entries are keyed by raw Address; storage-trie reads take the addressHash.
        ValueHash256 addrHash = addr.ToAccountPath;

#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(persisted.TryGetAccount(addr, out Account? acc1), Is.True);
#pragma warning restore NUnit2045
        Assert.That(acc1, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(acc1!.Balance, Is.EqualTo(expectedAccount.Balance));
            Assert.That(acc1.Nonce, Is.EqualTo(expectedAccount.Nonce));

            Assert.That(persisted.TryGetSelfDestructFlag(addr), Is.EqualTo((bool?)true));
        }

        UInt256 probeIndex = (UInt256)(Math.Min(slotCount, 3));
        Assert.That(persisted.TryGetSlot(addr, probeIndex, out UInt256? slot1), Is.True);
        byte[] expectedSlotVal = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(expectedSlotVal.AsSpan(28, 4), (int)probeIndex);
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(slot1.GetValueOrDefault().ToBigEndian().AsSpan().SequenceEqual(expectedSlotVal), Is.True);
#pragma warning restore NUnit2045

        Assert.That(persisted.TryLoadStorageNodeRlp(addrHash, storagePath, out byte[]? nodeRlp1), Is.True);
        Assert.That(nodeRlp1, Is.EqualTo(storageNodeRlp));

        // Second pass: results must match.
        Assert.That(persisted.TryGetAccount(addr, out Account? acc2), Is.True);
        Assert.That(acc2!.Balance, Is.EqualTo(expectedAccount.Balance));
        Assert.That(persisted.TryGetSlot(addr, probeIndex, out UInt256? slot2), Is.True);
        Assert.That(slot2.GetValueOrDefault().ToBigEndian().AsSpan().SequenceEqual(expectedSlotVal), Is.True);

        // AdviseDontNeed advises the mmap range cold; the next reads re-fault any dropped page
        // and the binary search must still resolve correctly.
        persisted.AdviseDontNeed();
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(persisted.TryGetAccount(addr, out Account? acc3), Is.True);
#pragma warning restore NUnit2045
        Assert.That(acc3!.Nonce, Is.EqualTo(expectedAccount.Nonce));
        Assert.That(persisted.TryGetSlot(addr, probeIndex, out UInt256? slot3), Is.True);
        Assert.That(slot3.GetValueOrDefault().ToBigEndian().AsSpan().SequenceEqual(expectedSlotVal), Is.True);

        // Fresh miss for an unrelated address still works after AdviseDontNeed.
        Assert.That(persisted.TryGetAccount(TestItem.AddressB, out _), Is.False);
    }
}
