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
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
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
    private MemDb _codeDb = null!;
    private IFlatDbManager _flatDbManager = null!;
    private IFlatStateRootIndex _stateRootIndex = null!;
    private FlatSnapServer _server = null!;
    private Hash256 _rootHash = null!;
    private StateId _stateId;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb);
        _codeDb = new MemDb();

        byte[] rootRlp = BuildRootRlp(out _rootHash);
        _stateId = new StateId(0, _rootHash.ValueHash256);
        WriteStateTrieRoot(rootRlp);

        _flatDbManager = Substitute.For<IFlatDbManager>();
        _flatDbManager.GatherReadOnlySnapshotBundle(_stateId)
            .Returns(_ => new ReadOnlySnapshotBundle(new SnapshotPooledList(0), _persistence.CreateReader(), recordDetailedMetrics: false));

        _stateRootIndex = Substitute.For<IFlatStateRootIndex>();
        _stateRootIndex.TryGetStateId(Arg.Any<Hash256>(), out Arg.Any<StateId>())
            .Returns(callInfo =>
            {
                callInfo[1] = _stateId;
                return true;
            });

        _server = new FlatSnapServer(_flatDbManager, _codeDb, _stateRootIndex, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _codeDb.Dispose();
        _columnsDb.Dispose();
    }

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
}
