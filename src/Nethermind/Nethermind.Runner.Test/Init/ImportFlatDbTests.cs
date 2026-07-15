// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Init;

[TestFixture]
public class ImportFlatDbTests
{
    private MemDb _trieDb = null!;
    private StateTree _stateTree = null!;
    private NodeStorage _nodeStorage = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;
    private Importer _importer = null!;

    [SetUp]
    public void SetUp()
    {
        _trieDb = new MemDb();
        _stateTree = new StateTree(new RawScopedTrieStore(_trieDb), LimboLogs.Instance);
        for (int i = 0; i < 5; i++)
        {
            _stateTree.Set(TestItem.GetRandomAddress(), TestItem.GenerateIndexedAccount(i + 1));
        }
        _stateTree.Commit();

        _nodeStorage = new NodeStorage(_trieDb);
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance);
        _importer = new Importer(_nodeStorage, _persistence, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _trieDb.Dispose();
        _columnsDb.Dispose();
    }

    private ImportFlatDb CreateStep(BlockHeader head)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithHeader(head).TestObject);
        return new ImportFlatDb(
            blockTree,
            _persistence,
            _nodeStorage,
            _importer,
            Substitute.For<IProcessExitSource>(),
            new FlatDbConfig(),
            LimboLogs.Instance);
    }

    [Test]
    public async Task Execute_WhenFlatDbIsFresh_ImportsHeadState()
    {
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis), "precondition: fresh flat db");
        }

        BlockHeader head = Build.A.BlockHeader.WithNumber(1).WithStateRoot(_stateTree.RootHash).TestObject;
        await CreateStep(head).Execute(CancellationToken.None);

        using IPersistence.IPersistenceReader after = _persistence.CreateReader();
        Assert.That(after.CurrentState, Is.EqualTo(new StateId(head)), "import should have run on a fresh flat db");
    }

    [Test]
    public async Task Execute_WhenFlatDbAlreadyPopulated_SkipsImport()
    {
        StateId existing = new(5, _stateTree.RootHash);
        using (IPersistence.IWriteBatch seed = _persistence.CreateWriteBatch(StateId.PreGenesis, existing))
        {
        }
        _persistence.Flush();

        BlockHeader head = Build.A.BlockHeader.WithNumber(10).WithStateRoot(_stateTree.RootHash).TestObject;
        await CreateStep(head).Execute(CancellationToken.None);

        using IPersistence.IPersistenceReader after = _persistence.CreateReader();
        Assert.That(after.CurrentState, Is.EqualTo(existing), "existing flat db state must not be overwritten");
    }
}
