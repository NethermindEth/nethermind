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
            Rlp.ValueDecoderContext ctx = new(rlp!);
            Assert.That(AccountDecoder.Instance.Decode(ref ctx), Is.EqualTo(expected));
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
}
