// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ImporterTests
{
    private MemDb _trieDb = null!;
    private StateTree _stateTree = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;
    private Importer _importer = null!;

    [SetUp]
    public void SetUp()
    {
        _trieDb = new MemDb();
        _stateTree = new StateTree(new RawScopedTrieStore(_trieDb), LimboLogs.Instance);
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance);
        _importer = new Importer(new NodeStorage(_trieDb), _persistence, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _trieDb.Dispose();
        _columnsDb.Dispose();
    }

    private (Address address, Account account)[] SeedTree(int count)
    {
        (Address, Account)[] accounts = new (Address, Account)[count];
        for (int i = 0; i < count; i++)
        {
            (Address addr, Account acc) = (TestItem.GetRandomAddress(), TestItem.GenerateIndexedAccount(i + 1));
            accounts[i] = (addr, acc);
            _stateTree.Set(addr, acc);
        }
        _stateTree.Commit();
        return accounts;
    }

    [Test]
    public async Task Copy_TransfersAllAccountsFromTrieToFlatPersistence()
    {
        (Address address, Account account)[] accounts = SeedTree(10);

        await _importer.Copy(new StateId(0, _stateTree.RootHash));

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        foreach ((Address addr, Account expected) in accounts)
        {
            byte[]? rlp = reader.GetAccountRaw(new Hash256(addr.ToAccountPath.Bytes));
            Assert.That(rlp, Is.Not.Null, $"account {addr} should have been imported");
            RlpReader ctx = new(rlp!);
            Assert.That(AccountDecoder.Slim.Decode(ref ctx), Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task Copy_AdvancesPersistenceCurrentStateToTarget()
    {
        SeedTree(3);
        StateId target = new(42, _stateTree.RootHash);

        await _importer.Copy(target);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(target));
    }

    [Test]
    public async Task Copy_PropagatesCancellation()
    {
        SeedTree(3);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThatAsync(async () => await _importer.Copy(new StateId(0, _stateTree.RootHash), cts.Token),
            Throws.InstanceOf<System.OperationCanceledException>());
    }

    [Test]
    public async Task Copy_ReportsFullProgressWhenTheTraversalCompletes()
    {
        SeedTree(10);
        (Importer importer, InterfaceLogger logger) = CreateImporterWithInfoLogger();

        await importer.Copy(new StateId(0, _stateTree.RootHash));

        logger.Received(1).Info(Arg.Is<string>(line => line.Contains("Flat Import") && line.Contains("100.00 %")));
    }

    [Test]
    public async Task Copy_DoesNotReportFullProgressWhenTheTraversalFails()
    {
        (Importer importer, InterfaceLogger logger) = CreateImporterWithInfoLogger();

        // Unknown state root, so the traversal fails on its first node
        await Assert.ThatAsync(async () => await importer.Copy(new StateId(0, TestItem.KeccakA)),
            Throws.InstanceOf<TrieException>());

        logger.DidNotReceive().Info(Arg.Is<string>(line => line.Contains("100.00 %")));
    }

    [Test]
    public async Task Copy_DoesNotReportFullProgressWhenCancelledDuringTheTraversal()
    {
        // Fixed addresses give depth-1 leaves, so a progress line is written mid-traversal
        _stateTree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(1));
        _stateTree.Set(TestItem.AddressB, TestItem.GenerateIndexedAccount(2));
        _stateTree.Set(TestItem.AddressC, TestItem.GenerateIndexedAccount(3));
        _stateTree.Commit();
        using CancellationTokenSource cts = new();
        (Importer importer, InterfaceLogger logger) = CreateImporterWithInfoLogger();
        logger.When(l => l.Info(Arg.Is<string>(line => line.Contains("Flat Import")))).Do(_ => cts.Cancel());

        await Assert.ThatAsync(async () => await importer.Copy(new StateId(0, _stateTree.RootHash), cts.Token),
            Throws.InstanceOf<System.OperationCanceledException>());

        logger.DidNotReceive().Info(Arg.Is<string>(line => line.Contains("100.00 %")));
    }

    private (Importer importer, InterfaceLogger logger) CreateImporterWithInfoLogger()
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        return (new Importer(new NodeStorage(_trieDb), _persistence, new OneLoggerLogManager(new ILogger(logger))), logger);
    }
}
