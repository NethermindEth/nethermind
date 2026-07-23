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
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync.Snap;

[TestFixture]
public class FlatSnapServerTests
{
    private const int RequestCount = 5000;

    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;
    private IFlatDbManager _flatDbManager = null!;
    private IFlatStateRootIndex _stateRootIndex = null!;
    private FlatSnapServer _server = null!;
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

        _server = new FlatSnapServer(_flatDbManager, _stateRootIndex, LimboLogs.Instance);
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

        Assert.That(result.Count, Is.LessThan(RequestCount));
    }

    [Test]
    public void GetTrieNodes_RespectsHardResponseByteLimitInStorageLoop()
    {
        // Rebuild state with a single account whose storage root is persisted, so the
        // storage inner loop is actually reached (state-tree navigation needs the leaf, not
        // just the root).
        Hash256 addressHash = Keccak.Compute(TestItem.AddressA.Bytes);
        Hash256 storageRoot = BuildAndPersistStorageRoot(addressHash, out byte[] storageRootRlp);
        byte[] stateRootRlp = BuildSingleAccountStateRoot(addressHash, storageRoot, out _rootHash);
        _stateId = new StateId(0, _rootHash.ValueHash256);

        _flatDbManager.GatherReadOnlySnapshotBundle(_stateId)
            .Returns(_ => new ReadOnlySnapshotBundle(new SnapshotPooledList(0), _persistence.CreateReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty()));

        WriteState(stateRootRlp, addressHash, storageRootRlp);

        // Single PathGroup with one account path followed by RequestCount empty storage paths.
        // Each iteration returns the (non-empty) storage root, so the inner reqStorage loop
        // must hit the byte limit before completing.
        byte[][] group = new byte[RequestCount + 1][];
        group[0] = addressHash.Bytes.ToArray();
        for (int i = 1; i <= RequestCount; i++) group[i] = [];

        using RlpPathGroupList pathSet = PathGroup.EncodeToRlpPathGroupList([new PathGroup { Group = group }]);
        using IByteArrayList result = _server.GetTrieNodes(pathSet, _rootHash, CancellationToken.None)!;

        Assert.That(result.Count, Is.LessThan(RequestCount));
    }

    [Test]
    public void GetTrieNodes_EmptyPathGroup_ReturnsNull()
    {
        // A path group with no paths is a malformed request; the whole response must be null.
        using RlpPathGroupList pathSet = PathGroup.EncodeToRlpPathGroupList([new PathGroup { Group = [] }]);

        IByteArrayList? result = _server.GetTrieNodes(pathSet, _rootHash, CancellationToken.None);

        Assert.That(result, Is.Null);
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

    private static Hash256 BuildAndPersistStorageRoot(Hash256 addressHash, out byte[] rootRlp)
    {
        using MemDb storageDb = new();
        RawScopedTrieStore storageStore = new(storageDb, addressHash);
        StorageTree storageTree = new(storageStore, Keccak.EmptyTreeHash, LimboLogs.Instance);

        // 32 populated slots produces a branch-heavy root well above the per-iteration size
        // we need to push the total response past HardResponseByteLimit (2 MB / RequestCount).
        for (int i = 0; i < 32; i++)
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
