// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
    private RawScopedTrieStore _trieStore = null!;
    private StateTree _stateTree = null!;
    private NodeStorage _nodeStorage = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _trieDb = new MemDb();
        _trieStore = new RawScopedTrieStore(_trieDb);
        _stateTree = new StateTree(_trieStore, LimboLogs.Instance);
        _nodeStorage = new NodeStorage(_trieDb);
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb);
    }

    [TearDown]
    public void TearDown()
    {
        _trieDb.Dispose();
        _columnsDb.Dispose();
    }

    private static (Address address, Account account)[] BuildAccounts(int count)
    {
        (Address, Account)[] result = new (Address, Account)[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = (TestItem.GetRandomAddress(), TestItem.GenerateIndexedAccount(i + 1));
        }
        return result;
    }

    [Test]
    public async Task Copy_TransfersAllAccountsFromTrieToFlatPersistence()
    {
        (Address address, Account account)[] accounts = BuildAccounts(10);
        foreach ((Address addr, Account acc) in accounts)
        {
            _stateTree.Set(addr, acc);
        }
        _stateTree.Commit();
        Hash256 root = _stateTree.RootHash;

        Importer importer = new(_nodeStorage, _persistence, LimboLogs.Instance);
        await importer.Copy(new StateId(0, root));

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        foreach ((Address addr, Account expected) in accounts)
        {
            byte[]? rlp = reader.GetAccountRaw(new Hash256(addr.ToAccountPath.Bytes));
            rlp.Should().NotBeNull($"account {addr} should have been imported");
            Rlp.ValueDecoderContext ctx = new(rlp);
            AccountDecoder.Instance.Decode(ref ctx).Should().Be(expected);
        }
    }

    [Test]
    public async Task Copy_AdvancesPersistenceCurrentStateToTarget()
    {
        (Address address, Account account)[] accounts = BuildAccounts(3);
        foreach ((Address addr, Account acc) in accounts) _stateTree.Set(addr, acc);
        _stateTree.Commit();

        StateId target = new(42, _stateTree.RootHash);
        Importer importer = new(_nodeStorage, _persistence, LimboLogs.Instance);
        await importer.Copy(target);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        reader.CurrentState.Should().Be(target);
    }

    [Test]
    public async Task Copy_PropagatesCancellation()
    {
        (Address address, Account account)[] accounts = BuildAccounts(3);
        foreach ((Address addr, Account acc) in accounts) _stateTree.Set(addr, acc);
        _stateTree.Commit();

        Importer importer = new(_nodeStorage, _persistence, LimboLogs.Instance);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThatAsync(async () => await importer.Copy(new StateId(0, _stateTree.RootHash), cts.Token),
            Throws.InstanceOf<System.OperationCanceledException>());
    }
}
