// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync.Snap;

[TestFixture]
public class SnapFlatStateServerTests
{
    private const int RequestCount = 5000;

    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;
    private IFlatDbManager _flatDbManager = null!;
    private IFlatStateRootIndex _stateRootIndex = null!;
    private SnapFlatStateServer _server = null!;
    private Hash256 _rootHash = null!;
    private StateId _stateId;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance);

        byte[] rootRlp = BuildRootRlp(out _rootHash);
        _stateId = new StateId(0, _rootHash.ValueHash256);
        WriteStateTrieRoot(rootRlp);

        _flatDbManager = Substitute.For<IFlatDbManager>();
        _flatDbManager.GatherReadOnlySnapshotBundle(_stateId)
            .Returns(_ => new ReadOnlySnapshotBundle(new SnapshotPooledList(0), _persistence.CreateReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty()));

        _stateRootIndex = Substitute.For<IFlatStateRootIndex>();
        _stateRootIndex.TryGetStateId(Arg.Any<Hash256>(), out Arg.Any<StateId>())
            .Returns(callInfo =>
            {
                callInfo[1] = _stateId;
                return true;
            });

        _server = new SnapFlatStateServer(_flatDbManager, _stateRootIndex, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    [Test]
    public void GetTrieNodes_RespectsHardResponseByteLimit()
    {
        PathGroup[] groups = new PathGroup[RequestCount];
        for (int i = 0; i < RequestCount; i++)
        {
            groups[i] = new PathGroup { Group = [[]] };
        }

        using RlpPathGroupList pathSet = PathGroup.EncodeToRlpPathGroupList(groups);
        using IByteArrayList result = _server.GetTrieNodes(pathSet, _rootHash, CancellationToken.None)!;

        // Below the lookup cap too, so this asserts the byte limit rather than that cap.
        Assert.That(result.Count, Is.LessThan(ISnapStateServer.MaxTrieNodeLookups));
    }

    [Test]
    public void GetTrieNodes_RespectsHardResponseByteLimitInStorageLoop()
    {
        // Single PathGroup with one account path followed by RequestCount empty storage paths.
        // Each iteration returns the (non-empty) storage root, so the inner reqStorage loop
        // must hit the byte limit before completing.
        using IByteArrayList result = RequestStoragePaths(slotCount: 256, storagePath: []);

        // Below the lookup cap too, so this asserts the byte limit rather than that cap.
        Assert.That(result.Count, Is.LessThan(ISnapStateServer.MaxTrieNodeLookups - 1));
    }

    [Test]
    public void GetTrieNodes_BoundsLookupsForPathsThatResolveToNothing()
    {
        // Nibbles a,b,c,d in compact form, against a storage trie that is a single leaf: every
        // one of them resolves to no node at all and contributes nothing to the response size.
        using IByteArrayList result = RequestStoragePaths(slotCount: 1, storagePath: [0x00, 0xab, 0xcd]);

        using (Assert.EnterMultipleScope())
        {
            // The account lookup takes the first of the budget, the storage lookups the rest.
            Assert.That(result.Count, Is.EqualTo(ISnapStateServer.MaxTrieNodeLookups - 1));
            Assert.That(result[0].Length, Is.Zero);
        }
    }

    [Test]
    public void GetTrieNodes_EmptyPathGroup_ReturnsNull()
    {
        // A path group with no paths is a malformed request; the whole response must be null.
        using RlpPathGroupList pathSet = PathGroup.EncodeToRlpPathGroupList([new PathGroup { Group = [] }]);

        IByteArrayList? result = _server.GetTrieNodes(pathSet, _rootHash, CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetStorageRanges_ReturnsEmpty_WhenIndexedStateIsConcurrentlyRemoved()
    {
        _flatDbManager.GatherReadOnlySnapshotBundle(_stateId)
            .Returns(_ => throw new StateNotRetainedException($"State {_stateId} no longer exists; concurrently removed."));

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) =
            _server.GetStorageRanges(_rootHash, [], null, null, 1, CancellationToken.None);
        using (slots)
        using (proofs)
        {
            Assert.That(slots, Is.Empty);
            Assert.That(proofs, Is.Empty);
        }
    }

    /// <summary>
    /// Rebuilds the state with a single account whose storage root is persisted, so the storage
    /// inner loop is actually reached (state-tree navigation needs the leaf, not just the root),
    /// then asks for <see cref="RequestCount"/> copies of <paramref name="storagePath"/>.
    /// </summary>
    private IByteArrayList RequestStoragePaths(int slotCount, byte[] storagePath)
    {
        Hash256 addressHash = Keccak.Compute(TestItem.AddressA.Bytes);
        Hash256 storageRoot = BuildAndPersistStorageRoot(addressHash, slotCount, out byte[] storageRootRlp);
        byte[] stateRootRlp = BuildSingleAccountStateRoot(addressHash, storageRoot, out _rootHash);
        _stateId = new StateId(0, _rootHash.ValueHash256);

        _flatDbManager.GatherReadOnlySnapshotBundle(_stateId)
            .Returns(_ => new ReadOnlySnapshotBundle(new SnapshotPooledList(0), _persistence.CreateReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty()));

        WriteState(stateRootRlp, addressHash, storageRootRlp);

        byte[][] group = new byte[RequestCount + 1][];
        group[0] = addressHash.Bytes.ToArray();
        for (int i = 1; i <= RequestCount; i++) group[i] = storagePath;

        using RlpPathGroupList pathSet = PathGroup.EncodeToRlpPathGroupList([new PathGroup { Group = group }]);
        return _server.GetTrieNodes(pathSet, _rootHash, CancellationToken.None)!;
    }

    private static byte[] BuildRootRlp(out Hash256 rootHash)
    {
        using MemDb trieDb = new();
        RawScopedTrieStore trieStore = new(trieDb);
        StateTree tree = new(trieStore, LimboLogs.Instance);

        for (int i = 0; i < 1000; i++)
        {
            tree.Set(Keccak.Compute(i.ToBigEndianByteArray()), Build.An.Account.WithBalance((UInt256)i).TestObject);
        }

        tree.Commit();
        rootHash = tree.RootHash;
        return tree.GetNodeByPath([], rootHash)!;
    }

    private static Hash256 BuildAndPersistStorageRoot(Hash256 addressHash, int slotCount, out byte[] rootRlp)
    {
        using MemDb storageDb = new();
        RawScopedTrieStore storageStore = new(storageDb, addressHash);
        StorageTree storageTree = new(storageStore, Keccak.EmptyTreeHash, LimboLogs.Instance);

        // Enough slots gives a root branch whose 16 children are all hash references, the widest
        // node the loop can repeat; a single slot gives a leaf root that any non-empty path
        // misses without touching a child node.
        for (int i = 0; i < slotCount; i++)
        {
            storageTree.Set(Keccak.Compute(i.ToBigEndianByteArray()).Bytes, Rlp.Encode((UInt256)i + 1));
        }

        storageTree.Commit();
        rootRlp = storageTree.GetNodeByPath([], storageTree.RootHash)!;
        return storageTree.RootHash;
    }

    private static byte[] BuildSingleAccountStateRoot(Hash256 addressHash, Hash256 storageRoot, out Hash256 rootHash)
    {
        using MemDb trieDb = new();
        RawScopedTrieStore trieStore = new(trieDb);
        StateTree tree = new(trieStore, LimboLogs.Instance);

        Account account = Build.An.Account.WithBalance(1).WithStorageRoot(storageRoot).TestObject;
        tree.Set(addressHash, account);

        tree.Commit();
        rootHash = tree.RootHash;
        return tree.GetNodeByPath([], rootHash)!;
    }

    private void WriteStateTrieRoot(byte[] rootRlp)
    {
        StateId currentState;
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            currentState = reader.CurrentState;
        }

        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(currentState, _stateId, WriteFlags.DisableWAL);
        batch.SetStateTrieNode(TreePath.Empty, rootRlp);
    }

    private void WriteState(byte[] stateRootRlp, Hash256 storageAddressHash, byte[] storageRootRlp)
    {
        StateId currentState;
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            currentState = reader.CurrentState;
        }

        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(currentState, _stateId, WriteFlags.DisableWAL);
        batch.SetStateTrieNode(TreePath.Empty, stateRootRlp);
        batch.SetStorageTrieNode(storageAddressHash, TreePath.Empty, storageRootRlp);
    }
}
