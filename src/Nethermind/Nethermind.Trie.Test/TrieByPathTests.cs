// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
public class TrieByPathTests
{
    private ILogger _logger;
    private ILogManager _logManager;
    private Random _random = new();

    [SetUp]
    public void SetUp()
    {
        _logManager = LimboLogs.Instance;
        //_logManager = new NUnitLogManager(LogLevel.Trace);
        _logger = _logManager.GetClassLogger();
    }

    [TearDown]
    public void TearDown()
    {
    }

    private static readonly byte[] _longLeaf1
        = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000000000000000000001");

    private static readonly byte[] _longLeaf2
        = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000000000000000000002");

    private static readonly byte[] _longLeaf3
        = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000000000000000000003");

    private static byte[] _keyAccountA = KeccakHash.ComputeHashBytes(TestItem.AddressA.Bytes);
    private static byte[] _keyAccountB = KeccakHash.ComputeHashBytes(TestItem.AddressB.Bytes);
    private static byte[] _keyAccountC = KeccakHash.ComputeHashBytes(TestItem.AddressC.Bytes);
    private static byte[] _keyAccountD = KeccakHash.ComputeHashBytes(TestItem.AddressD.Bytes);
    private static byte[] _keyAccountE = KeccakHash.ComputeHashBytes(TestItem.AddressE.Bytes);
    private static byte[] _keyAccountF = KeccakHash.ComputeHashBytes(TestItem.AddressF.Bytes);


    private AccountDecoder _decoder = new AccountDecoder();
    private readonly byte[] _account0 = Rlp.Encode(Build.An.Account.WithBalance(0).TestObject).Bytes;
    private readonly byte[] _account1 = Rlp.Encode(Build.An.Account.WithBalance(1).TestObject).Bytes;
    private readonly byte[] _account2 = Rlp.Encode(Build.An.Account.WithBalance(2).TestObject).Bytes;
    private readonly byte[] _account3 = Rlp.Encode(Build.An.Account.WithBalance(3).TestObject).Bytes;

    [Test]
    public void Single_leaf()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Commit(0);

        // (root) -> leaf | leaf
        // memDb.Keys.Should().HaveCount(2);
    }

    [Test]
    public void Single_leaf_update_same_block()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new PatriciaTree(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountA, _longLeaf2);
        patriciaTree.Commit(0);

        // (root) -> leaf | leaf
        // memDb.Keys.Should().HaveCount(2);

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().NotBeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountA).Should().BeEquivalentTo(_longLeaf2);
    }

    [Test]
    public void Single_leaf_update_next_blocks()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Commit(0);
        patriciaTree.Set(_keyAccountA, _longLeaf2);
        patriciaTree.Commit(1);
        patriciaTree.UpdateRootHash();
        patriciaTree.Get(_keyAccountA).Should().NotBeEquivalentTo(_longLeaf1);
        patriciaTree.Get(_keyAccountA).Should().BeEquivalentTo(_longLeaf2);

        // leaf (root)
        // memDb.Keys.Should().HaveCount(2);

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().NotBeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountA).Should().BeEquivalentTo(_longLeaf2);
    }

    [Test]
    public void Single_leaf_delete_same_block()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountA, Array.Empty<byte>());
        patriciaTree.Commit(0);

        // leaf (root)
        // memDb.Keys.Should().HaveCount(0);

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeNull();
    }

    [Test]
    public void Single_leaf_delete_next_block()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Commit(0);
        patriciaTree.Set(_keyAccountA, Array.Empty<byte>());
        patriciaTree.Commit(1);
        patriciaTree.UpdateRootHash();

        // leaf (root)
        // memDb.Keys.Should().HaveCount(1);

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeNull();
    }

    [Test]
    public void Single_leaf_and_keep_for_multiple_dispatches_then_delete()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, LimboLogs.Instance);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Commit(0);
        patriciaTree.Commit(1);
        patriciaTree.Commit(2);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Commit(3);
        patriciaTree.Commit(4);
        patriciaTree.Set(_keyAccountA, Array.Empty<byte>());
        patriciaTree.Commit(5);
        patriciaTree.Set(_keyAccountB, _longLeaf2);
        patriciaTree.Commit(6);
        patriciaTree.Commit(7);
        patriciaTree.Commit(8);
        patriciaTree.Commit(9);
        patriciaTree.Commit(10);
        patriciaTree.Commit(11);
        patriciaTree.Set(_keyAccountB, Array.Empty<byte>());
        patriciaTree.Commit(12);
        patriciaTree.Commit(13);
        patriciaTree.UpdateRootHash();

        // leaf (root)
        // memDb.Keys.Should().HaveCount(2);

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeNull();
        checkTree.Get(_keyAccountB).Should().BeNull();
    }

    [Test]
    public void Branch_with_branch_and_leaf()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountB, _longLeaf1);
        patriciaTree.Set(_keyAccountC, _longLeaf1);
        patriciaTree.Commit(0);

        // leaf (root)
        // memDb.Keys.Should().HaveCount(6);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountB).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountC).Should().BeEquivalentTo(_longLeaf1);
    }

    // [Test]
    // public void When_an_inlined_leaf_is_cloned_and_the_extended_version_is_no_longer_inlined()
    // {
    //     throw new NotImplementedException();
    // }
    //
    // [Test]
    // public void When_a_node_is_loaded_from_the_DB_as_unknown_and_unreferenced()
    // {
    //     throw new NotImplementedException();
    // }

    [Test]
    public void Branch_with_branch_and_leaf_then_deleted()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountB, _longLeaf1);
        patriciaTree.Set(_keyAccountC, _longLeaf1);
        patriciaTree.Commit(0);
        patriciaTree.Set(_keyAccountA, Array.Empty<byte>());
        patriciaTree.Set(_keyAccountB, Array.Empty<byte>());
        patriciaTree.Set(_keyAccountC, Array.Empty<byte>());
        patriciaTree.Commit(1);
        patriciaTree.UpdateRootHash();

        // leaf (root)
        // memDb.Keys.Should().HaveCount(6);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeNull();
        checkTree.Get(_keyAccountB).Should().BeNull();
        checkTree.Get(_keyAccountC).Should().BeNull();
    }

    public void Test_add_many(int i)
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, Keccak.EmptyTreeHash, true, true, _logManager);

        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j);
            patriciaTree.Set(key.Bytes, value);
        }

        patriciaTree.Commit(0);
        patriciaTree.UpdateRootHash();

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j);
            checkTree.Get(key.Bytes).Should().BeEquivalentTo(value, $@"{i} {j}");
        }
    }

    public void Test_try_delete_and_read_missing_nodes(int i)
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, Keccak.EmptyTreeHash, true, true, _logManager);

        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j);
            patriciaTree.Set(key.Bytes, value);
        }

        // delete missing
        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j + 100];
            patriciaTree.Set(key.Bytes, Array.Empty<byte>());
        }

        patriciaTree.Commit(0);
        patriciaTree.UpdateRootHash();

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);

        // confirm nothing deleted
        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j);
            checkTree.Get(key.Bytes).Should().BeEquivalentTo(value, $@"{i} {j}");
        }

        // read missing
        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j + 100];
            checkTree.Get(key.Bytes).Should().BeNull();
        }
    }

    public void Test_update_many(int i)
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);

        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j);
            patriciaTree.Set(key.Bytes, value);
        }

        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);
            patriciaTree.Set(key.Bytes, value);
        }

        patriciaTree.Commit(0);
        patriciaTree.UpdateRootHash();

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);
            checkTree.Get(key.Bytes).Should().BeEquivalentTo(value, $@"{i} {j}");
        }
    }

    public void Test_update_many_next_block(int i)
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);

        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j);
            patriciaTree.Set(key.Bytes, value);
        }

        patriciaTree.Commit(0);

        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);
            patriciaTree.Set(key.Bytes, value);
            _logger.Trace($"Setting {key.Bytes.ToHexString()} = {value.ToHexString()}");
        }

        patriciaTree.Commit(1);
        patriciaTree.UpdateRootHash();

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);

            _logger.Trace($"Checking {key.Bytes.ToHexString()} = {value.ToHexString()}");
            checkTree.Get(key.Bytes).Should().BeEquivalentTo(value, $@"{i} {j}");
        }
    }

    public void Test_add_and_delete_many_same_block(int i)
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);

        for (int j = 0; j < i; j++)
        {
            _logger.Trace($"  set {j}");
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j);
            patriciaTree.Set(key.Bytes, value);
        }

        for (int j = 0; j < i; j++)
        {
            _logger.Trace($"  delete {j}");
            Keccak key = TestItem.Keccaks[j];
            patriciaTree.Set(key.Bytes, Array.Empty<byte>());
        }

        patriciaTree.Commit(0);
        patriciaTree.UpdateRootHash();

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            checkTree.Get(key.Bytes).Should().BeNull($@"{i} {j}");
        }
    }

    public void Test_add_and_delete_many_next_block(int i)
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);

        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            byte[] value = TestItem.GenerateIndexedAccountRlp(j);
            patriciaTree.Set(key.Bytes, value);
        }

        patriciaTree.Commit(0);

        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            patriciaTree.Set(key.Bytes, Array.Empty<byte>());
        }

        patriciaTree.Commit(1);
        patriciaTree.UpdateRootHash();

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        for (int j = 0; j < i; j++)
        {
            Keccak key = TestItem.Keccaks[j];
            checkTree.Get(key.Bytes).Should().BeNull($@"{i} {j}");
        }
    }

    [Test]
    public void Big_test()
    {
        // there was a case that was failing only at iteration 85 (before you change it to a smaller number)

        for (int i = 0; i < 100; i++)
        {
            Console.WriteLine(i);
            _logger.Trace(i.ToString());
            Test_add_many(i);
            Test_update_many(i);
            Test_update_many_next_block(i);
            Test_add_and_delete_many_same_block(i);
            Test_add_and_delete_many_next_block(i);
            Test_try_delete_and_read_missing_nodes(i);
        }
    }

    [Test]
    public void Two_branches_exactly_same_leaf()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountB, _longLeaf1);
        patriciaTree.Set(_keyAccountC, _longLeaf1);
        patriciaTree.Set(_keyAccountD, _longLeaf1);
        patriciaTree.Commit(0);

        // leaf (root)
        // memDb.Keys.Should().HaveCount(5);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountB).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountC).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountD).Should().BeEquivalentTo(_longLeaf1);
    }

    [Test]
    public void Two_branches_exactly_same_leaf_then_one_removed()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, LimboLogs.Instance);

        PatriciaTree patriciaTree = new StateTree(trieStore, _logManager);

        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountB, _longLeaf1);
        patriciaTree.Set(_keyAccountC, _longLeaf1);
        patriciaTree.Set(_keyAccountD, _longLeaf1);
        patriciaTree.Set(_keyAccountA, Array.Empty<byte>());
        patriciaTree.Commit(0);
        patriciaTree.Get(_keyAccountA).Should().BeNull();
        patriciaTree.Get(_keyAccountB).Should().BeEquivalentTo(_longLeaf1);
        patriciaTree.Get(_keyAccountC).Should().BeEquivalentTo(_longLeaf1);
        patriciaTree.Get(_keyAccountD).Should().BeEquivalentTo(_longLeaf1);

        // leaf (root)
        // memDb.Keys.Should().HaveCount(6);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeNull();
        checkTree.Get(_keyAccountB).Should().BeEquivalentTo(_longLeaf1);
        // checkTree.Get(_keyAccountC).Should().BeEquivalentTo(_longLeaf1);
        // checkTree.Get(_keyAccountD).Should().BeEquivalentTo(_longLeaf1);
    }

    private static PatriciaTree CreateCheckTree(MemDb memDb, PatriciaTree patriciaTree)
    {
        PatriciaTree checkTree = new(memDb, capability: patriciaTree.Capability);
        checkTree.RootHash = patriciaTree.RootHash;
        return checkTree;
    }

    [Test]
    public void Extension_with_branch_with_two_different_children()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountB, _longLeaf2);
        patriciaTree.Commit(0);
        // memDb.Keys.Should().HaveCount(4);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountB).Should().BeEquivalentTo(_longLeaf2);
    }

    [Test]
    public void Extension_with_branch_with_two_same_children()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountB, _longLeaf1);
        patriciaTree.Commit(0);
        // memDb.Keys.Should().HaveCount(4);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountB).Should().BeEquivalentTo(_longLeaf1);
    }

    [Test]
    public void When_branch_with_two_different_children_change_one_and_change_back_next_block()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountB, _longLeaf2);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(0);
        patriciaTree.Set(_keyAccountA, _longLeaf3);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(1);

        // extension
        // branch
        // leaf x 2
        // memDb.Keys.Should().HaveCount(4);
    }

    [Test]
    public void When_branch_with_two_same_children_change_one_and_change_back_next_block()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountB, _longLeaf1);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(0);
        patriciaTree.Set(_keyAccountA, _longLeaf3);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(1);

        // memDb.Keys.Should().HaveCount(4);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountB).Should().BeEquivalentTo(_longLeaf1);
    }

    [Test]
    public void Extension_branch_extension_and_leaf_then_branch_leaf_leaf()
    {
        /* R
           E - - - - - - - - - - - - - - -
           B B B B B B B B B B B B B B B B
           E L - - - - - - - - - - - - - -
           E - - - - - - - - - - - - - - -
           B B B B B B B B B B B B B B B B
           L L - - - - - - - - - - - - - - */

        byte[] key1 = Bytes.FromHexString("000000100000000aa").PadLeft(32);
        byte[] key2 = Bytes.FromHexString("000000100000000bb").PadLeft(32);
        byte[] key3 = Bytes.FromHexString("000000200000000cc").PadLeft(32);

        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(key1, _longLeaf1);
        patriciaTree.Set(key2, _longLeaf1);
        patriciaTree.Set(key3, _longLeaf1);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(0);

        // memDb.Keys.Should().HaveCount(7);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(key1).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(key2).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(key3).Should().BeEquivalentTo(_longLeaf1);
    }

    [Test]
    public void Connect_extension_with_extension()
    {
        /* to test this case we need something like this initially */
        /* R
           E - - - - - - - - - - - - - - -
           B B B B B B B B B B B B B B B B
           E L - - - - - - - - - - - - - -
           E - - - - - - - - - - - - - - -
           B B B B B B B B B B B B B B B B
           L L - - - - - - - - - - - - - - */

        /* then we delete the leaf (marked as X) */
        /* R
           B B B B B B B B B B B B B B B B
           E X - - - - - - - - - - - - - -
           E - - - - - - - - - - - - - - -
           B B B B B B B B B B B B B B B B
           L L - - - - - - - - - - - - - - */

        /* and we end up with an extended extension replacing what was previously a top-level branch*/
        /* R
           E
           E
           E - - - - - - - - - - - - - - -
           B B B B B B B B B B B B B B B B
           L L - - - - - - - - - - - - - - */

        byte[] key1 = Bytes.FromHexString("000000100000000aa").PadLeft(32);
        byte[] key2 = Bytes.FromHexString("000000100000000bb").PadLeft(32);
        byte[] key3 = Bytes.FromHexString("000000200000000cc").PadLeft(32);

        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(key1, _longLeaf1);
        patriciaTree.Set(key2, _longLeaf1);
        patriciaTree.Set(key3, _longLeaf1);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(0);
        patriciaTree.Set(key3, Array.Empty<byte>());
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(1);

        // memDb.Keys.Should().HaveCount(8);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(key1).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(key2).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(key3).Should().BeNull();
    }

    [Test]
    public void When_two_branches_with_two_same_children_change_one_and_change_back_next_block()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.Set(_keyAccountB, _longLeaf1);
        patriciaTree.Set(_keyAccountC, _longLeaf1);
        patriciaTree.Set(_keyAccountD, _longLeaf1);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(0);
        patriciaTree.Set(_keyAccountA, _longLeaf3);
        patriciaTree.Set(_keyAccountA, _longLeaf1);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(1);

        // memDb.Keys.Should().HaveCount(5);
        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(_keyAccountA).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountB).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountC).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(_keyAccountD).Should().BeEquivalentTo(_longLeaf1);
    }

    [Test]
    public void Persist_inlined_node()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);
        PatriciaTree patriciaTree = new(trieStore, _logManager);

        byte[] smallLeafValue = Bytes.FromHexString("00000010000000aa");
        byte[] smallLeafValue2 = Bytes.FromHexString("00000010000000dd");

        byte[] key1 = Bytes.FromHexString("abc000100000000aa").PadLeft(32);
        byte[] key2 = Bytes.FromHexString("abc000200000000bb").PadLeft(32);

        patriciaTree.Set(key1, smallLeafValue);
        patriciaTree.Set(key2, _longLeaf1);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(0);

        //check inlined node was persisted under its full path
        byte[] inlinedNodeData = patriciaTree.Get(key1);
        Assert.That(inlinedNodeData, Is.EqualTo(smallLeafValue).Using<byte[]>(Bytes.Comparer));

        byte[] key3 = Bytes.FromHexString("abc00010000f000aa").PadLeft(32);

        //make an update enforcing inlined leaf to expand L -> E->B-LL
        //ensure that inlined leaf is accessible via tree traversal as well
        patriciaTree.Set(key3, smallLeafValue2);
        patriciaTree.UpdateRootHash();
        patriciaTree.Commit(1);

        PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
        checkTree.Get(key1).Should().BeEquivalentTo(smallLeafValue);
        checkTree.Get(key2).Should().BeEquivalentTo(_longLeaf1);
        checkTree.Get(key3).Should().BeEquivalentTo(smallLeafValue2);
    }

    [Test]
    public void Request_deletion_for_leaf()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);

        Span<byte> fullPathNibbles = stackalloc byte[64];
        Nibbles.BytesToNibbleBytes(Bytes.FromHexString("0x12345600000033333333333333300ee000000555555555555555550000abcdef"), fullPathNibbles);

        trieStore.RequestDeletionForLeaf(fullPathNibbles.Slice(0, 5), fullPathNibbles);
        //TODO - do some actuall asserts
    }

    [Test]
    public void Request_deletion_for_extension()
    {
        MemColumnsDb<StateColumns> memDb = new();
        using TrieStoreByPath trieStore = new(memDb, _logManager);

        Span<byte> fullPathNibbles = stackalloc byte[5] { 3, 13, 3, 2, 13 };
        Span<byte> extensionKey = stackalloc byte[2] { 7, 10 };

        trieStore.RequestDeletionForExtension(fullPathNibbles, extensionKey);
        //TODO - do some actuall asserts
    }

    [TestCase(256, 128, 128, 32)]
    [TestCase(128, 128, 8, 8)]
    [TestCase(4, 16, 4, 4)]
    public void Fuzz_accounts(
        int accountsCount,
        int blocksCount,
        int uniqueValuesCount,
        int lookupLimit)
    {
        string fileName = Path.GetTempFileName();
        //string fileName = "C:\\Temp\\fuzz.txt";
        _logger.Info(
            $"Fuzzing with accounts: {accountsCount}, " +
            $"blocks {blocksCount}, " +
            $"values: {uniqueValuesCount}, " +
            $"lookup: {lookupLimit} into file {fileName}");

        using FileStream fileStream = new(fileName, FileMode.Create);
        using StreamWriter streamWriter = new(fileStream);

        Queue<Keccak> rootQueue = new();

        MemColumnsDb<StateColumns> memDb = new();

        using TrieStoreByPath trieStore = new(memDb, Persist.IfBlockOlderThan(lookupLimit), _logManager);
        StateTreeByPath patriciaTree = new(trieStore, _logManager);

        byte[][] accounts = new byte[accountsCount][];
        byte[][] randomValues = new byte[uniqueValuesCount][];

        for (int i = 0; i < randomValues.Length; i++)
        {
            bool isEmptyValue = _random.Next(0, 2) == 0;
            if (isEmptyValue)
            {
                randomValues[i] = Array.Empty<byte>();
            }
            else
            {
                randomValues[i] = TestItem.GenerateRandomAccountRlp();
            }
        }

        for (int accountIndex = 0; accountIndex < accounts.Length; accountIndex++)
        {
            byte[] key = new byte[32];
            ((UInt256)accountIndex).ToBigEndian(key);
            accounts[accountIndex] = key;
        }

        for (int blockNumber = 0; blockNumber < blocksCount; blockNumber++)
        {
            bool isEmptyBlock = _random.Next(5) == 0;
            if (!isEmptyBlock)
            {
                for (int i = 0; i < Math.Max(1, accountsCount / 8); i++)
                {
                    int randomAccountIndex = _random.Next(accounts.Length);
                    int randomValueIndex = _random.Next(randomValues.Length);

                    byte[] account = accounts[randomAccountIndex];
                    byte[] value = randomValues[randomValueIndex];

                    streamWriter.WriteLine(
                        $"Block {blockNumber} - setting {account.ToHexString()} = {value.ToHexString()}");
                    patriciaTree.Set(account, value);
                }
            }

            streamWriter.WriteLine(
                $"Commit block {blockNumber} | empty: {isEmptyBlock}");
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(blockNumber);
            rootQueue.Enqueue(patriciaTree.RootHash);
        }

        streamWriter.Flush();
        fileStream.Seek(0, SeekOrigin.Begin);

        streamWriter.WriteLine($"DB size: {memDb.Keys.Count}");
        _logger.Info($"DB size: {memDb.Keys.Count}");

        int verifiedBlocks = 0;

        int omitted = 0;
        do
        {
            rootQueue.TryDequeue(out Keccak _);
            omitted++;
        } while (omitted < trieStore.LastPersistedBlockNumber);

        while (rootQueue.TryDequeue(out Keccak currentRoot))
        {
            try
            {
                patriciaTree.RootHash = currentRoot;
                for (int i = 0; i < accounts.Length; i++)
                {
                    patriciaTree.Get(accounts[i]);
                }

                _logger.Info($"Verified positive {verifiedBlocks}");
            }
            catch (Exception ex)
            {
                if (verifiedBlocks % lookupLimit == 0)
                {
                    throw new InvalidDataException(ex.ToString());
                }
                else
                {
                    _logger.Info($"Verified negative {verifiedBlocks}");
                }
            }

            verifiedBlocks++;
        }
    }

    [TestCase(256, 128, 128, 32, null)]
    [TestCase(128, 128, 8, 8, null)]
    [TestCase(4, 16, 4, 4, null)]
    [TestCase(16, 32, 16, 4, null)]
    public void Fuzz_accounts_with_reorganizations(
        int accountsCount,
        int blocksCount,
        int uniqueValuesCount,
        int lookupLimit,
        int? seed)
    {
        int usedSeed = seed ?? _random.Next(int.MaxValue);
        _random = new Random(usedSeed);

        _logger.Info($"RANDOM SEED {usedSeed}");
        string fileName = Path.GetTempFileName();
        //string fileName = "C:\\Temp\\fuzz.txt";
        _logger.Info(
            $"Fuzzing with accounts: {accountsCount}, " +
            $"blocks {blocksCount}, " +
            $"values: {uniqueValuesCount}, " +
            $"lookup: {lookupLimit} into file {fileName}");

        using FileStream fileStream = new(fileName, FileMode.Create);
        using StreamWriter streamWriter = new(fileStream);

        Queue<Keccak> rootQueue = new();
        Stack<Tuple<int, Keccak>> rootStack = new();

        MemDb hashStateDb = new();
        using TrieStore hashTrieStore = new(hashStateDb, No.Pruning, Persist.IfBlockOlderThan(lookupLimit), _logManager);
        PatriciaTree hashTree = new(hashTrieStore, _logManager);

        MemColumnsDb<StateColumns> memDb = new();

        TestPathPersistanceStrategy strategy = new(lookupLimit, lookupLimit / 2);

        Reorganization.MaxDepth = 1;
        using TrieStoreByPath trieStore = new(memDb, strategy, _logManager);
        trieStore.ReorgBoundaryReached += (object? sender, ReorgBoundaryReached e) =>
        {
            strategy.LastPersistedBlockNumber = e.BlockNumber;
        };

        PatriciaTree patriciaTree = new(trieStore, _logManager);

        byte[][] accounts = new byte[accountsCount][];
        byte[][] randomValues = new byte[uniqueValuesCount][];

        for (int i = 0; i < randomValues.Length; i++)
        {
            bool isEmptyValue = _random.Next(0, 2) == 0;
            if (isEmptyValue)
            {
                randomValues[i] = Array.Empty<byte>();
            }
            else
            {
                randomValues[i] = TestItem.GenerateRandomAccountRlp();
            }
        }

        for (int accountIndex = 0; accountIndex < accounts.Length; accountIndex++)
        {
            byte[] key = new byte[32];
            ((UInt256)accountIndex).ToBigEndian(key);
            accounts[accountIndex] = key;
        }

        int blockCount = 0;
        for (int blockNumber = 0; blockNumber < blocksCount; blockNumber++)
        {
            _logger.Debug($"Starting loop {blockNumber}");
            int reorgDepth = _random.Next(Math.Min(5, blockCount - (int)trieStore.LastPersistedBlockNumber));
            _logger.Debug($"Reorganizing {reorgDepth}");

            for (int i = 0; i < reorgDepth; i++)
            {
                rootStack.Pop();
            }

            if (reorgDepth > 0)
            {
                _logger.Debug($"Reorg root hash - {rootStack.Peek()}");
                patriciaTree.ParentStateRootHash = rootStack.Peek().Item2;
                patriciaTree.RootHash = rootStack.Peek().Item2;
                hashTree.RootHash = rootStack.Peek().Item2;
            }

            blockCount = Math.Max(0, blockCount - reorgDepth);
            _logger.Debug($"Setting block count to {blockCount}");

            bool isEmptyBlock = _random.Next(5) == 0;
            if (!isEmptyBlock)
            {
                for (int i = 0; i < Math.Max(1, accountsCount / 8); i++)
                {
                    int randomAccountIndex = _random.Next(accounts.Length);
                    int randomValueIndex = _random.Next(randomValues.Length);

                    byte[] account = accounts[randomAccountIndex];
                    byte[] value = randomValues[randomValueIndex];

                    streamWriter.WriteLine(
                        $"Block {blockCount} - setting {account.ToHexString()} = {value.ToHexString()}");

                    patriciaTree.Set(account, value);
                    hashTree.Set(account, value);
                }
            }

            streamWriter.WriteLine(
                $"Commit block {blockCount} | empty: {isEmptyBlock}");
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(blockCount);

            hashTree.Commit(blockCount);

            rootQueue.Enqueue(patriciaTree.RootHash);
            rootStack.Push(new Tuple<int, Keccak>(blockCount, patriciaTree.RootHash));
            blockCount++;
            _logger.Debug($"Setting block count to {blockCount}");
        }

        streamWriter.Flush();
        fileStream.Seek(0, SeekOrigin.Begin);

        streamWriter.WriteLine($"DB size: {memDb.Keys.Count}");
        _logger.Info($"DB size: {memDb.Keys.Count}");

        int verifiedBlocks = 0;
        while (rootStack.TryPop(out Tuple<int, Keccak> currentRoot))
        {

            //don't check blocks prior to persisted - no history in DB
            if (currentRoot.Item1 < trieStore.LastPersistedBlockNumber)
                continue;
            patriciaTree.ParentStateRootHash = currentRoot.Item2;
            patriciaTree.RootHash = currentRoot.Item2;
            hashTree.RootHash = currentRoot.Item2;
            for (int i = 0; i < accounts.Length; i++)
            {
                byte[] path = patriciaTree.Get(accounts[i]);
                byte[] hash = hashTree.Get(accounts[i]);

                Assert.That(path, Is.EqualTo(hash).Using(Bytes.EqualityComparer));
            }

            _logger.Info($"Verified positive {verifiedBlocks}");

            verifiedBlocks++;
        }
    }

    [TestCase(96, 192, 96, 1541344441)]
    [TestCase(128, 256, 128, 988091870)]
    [TestCase(128, 256, 128, 2107374965)]
    [TestCase(128, 256, 128, null)]
    [TestCase(4, 16, 4, 1242692908)]
    [TestCase(8, 32, 8, 1543322391)]
    public void Fuzz_accounts_with_storage(
        int accountsCount,
        int blocksCount,
        int lookupLimit,
        int? seed)
    {
        int usedSeed = seed ?? _random.Next(int.MaxValue);
        //usedSeed = 1242692908;
        _random = new Random(usedSeed);
        _logger.Info($"RANDOM SEED {usedSeed}");

        string fileName = Path.GetTempFileName();
        //string fileName = "C:\\Temp\\fuzz.txt";
        _logger.Info(
            $"Fuzzing with accounts: {accountsCount}, " +
            $"blocks {blocksCount}, " +
            $"lookup: {lookupLimit} into file {fileName}");

        using FileStream fileStream = new(fileName, FileMode.Create);
        using StreamWriter streamWriter = new(fileStream);

        Queue<Keccak> rootQueue = new();

        MemColumnsDb<StateColumns> memDb = new();

        using TrieStoreByPath trieStore = new(memDb, Persist.IfBlockOlderThan(lookupLimit), _logManager);

        WorldState stateProvider = new(trieStore, new MemDb(), _logManager);

        Account[] accounts = new Account[accountsCount];
        Address[] addresses = new Address[accountsCount];

        for (int i = 0; i < accounts.Length; i++)
        {
            bool isEmptyValue = _random.Next(0, 2) == 0;
            if (isEmptyValue)
            {
                accounts[i] = Account.TotallyEmpty;
            }
            else
            {
                accounts[i] = TestItem.GenerateRandomAccount();
            }

            addresses[i] = TestItem.GetRandomAddress(_random);
        }

        for (int blockNumber = 0; blockNumber < blocksCount; blockNumber++)
        {
            bool isEmptyBlock = _random.Next(5) == 0;
            if (!isEmptyBlock)
            {
                for (int i = 0; i < Math.Max(1, accountsCount / 8); i++)
                {
                    int randomAddressIndex = _random.Next(addresses.Length);
                    int randomAccountIndex = _random.Next(accounts.Length);

                    Address address = addresses[randomAddressIndex];
                    Account account = accounts[randomAccountIndex];

                    if (stateProvider.AccountExists(address))
                    {
                        Account existing = stateProvider.GetAccount(address);
                        if (existing.Balance != account.Balance)
                        {
                            if (account.Balance > existing.Balance)
                            {
                                stateProvider.AddToBalance(
                                    address, account.Balance - existing.Balance, MuirGlacier.Instance);
                            }
                            else
                            {
                                stateProvider.SubtractFromBalance(
                                    address, existing.Balance - account.Balance, MuirGlacier.Instance);
                            }

                            stateProvider.IncrementNonce(address);
                        }

                        byte[] storage = new byte[1];
                        _random.NextBytes(storage);
                        stateProvider.Set(new StorageCell(address, 1), storage);
                    }
                    else if (!account.IsTotallyEmpty)
                    {
                        stateProvider.CreateAccount(address, account.Balance);

                        byte[] storage = new byte[1];
                        _random.NextBytes(storage);
                        stateProvider.Set(new StorageCell(address, 1), storage);
                    }
                }
            }

            streamWriter.WriteLine(
                $"Commit block {blockNumber} | empty: {isEmptyBlock}");

            stateProvider.Commit(MuirGlacier.Instance);

            stateProvider.CommitTree(blockNumber);
            rootQueue.Enqueue(stateProvider.StateRoot);
        }

        streamWriter.Flush();
        fileStream.Seek(0, SeekOrigin.Begin);

        streamWriter.WriteLine($"DB size: {memDb.Keys.Count}");
        _logger.Info($"DB size: {memDb.Keys.Count}");

        int verifiedBlocks = 0;
        int omitted = 0;
        do
        {
            rootQueue.TryDequeue(out Keccak _);
            omitted++;
        } while (omitted < trieStore.LastPersistedBlockNumber);

        while (rootQueue.TryDequeue(out Keccak currentRoot))
        {
            try
            {
                stateProvider.StateRoot = currentRoot;
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (stateProvider.AccountExists(addresses[i]))
                    {
                        for (int j = 0; j < 256; j++)
                        {
                            stateProvider.Get(new StorageCell(addresses[i], (UInt256)j));
                        }
                    }
                }

                _logger.Info($"Verified positive {verifiedBlocks}");
            }
            catch (Exception ex)
            {
                if (verifiedBlocks % lookupLimit == 0)
                {
                    throw new InvalidDataException(ex.ToString());
                }
                else
                {
                    _logger.Info($"Verified negative {verifiedBlocks} which is ok here");
                }
            }

            verifiedBlocks++;
        }
    }


    [Test()]
    [Explicit]
    public void ClearStorageThenCreateOnRocksDb()
    {
        var dirInfo = Directory.CreateTempSubdirectory();
        using ColumnsDb<StateColumns> stateDb = new(dirInfo.FullName, new RocksDbSettings("pathState", Path.Combine(dirInfo.FullName, "pathState")), new DbConfig(), _logManager, new StateColumns[] { StateColumns.State, StateColumns.Storage });

        using TrieStoreByPath pathTrieStore = new(stateDb, Persist.IfBlockOlderThan(2), _logManager);
        WorldState pathStateProvider = new(pathTrieStore, new MemDb(), _logManager);

        pathStateProvider.CreateAccount(TestItem.AddressA, 100);
        pathStateProvider.Set(new StorageCell(TestItem.AddressA, 100), new byte[] { 1 });
        pathStateProvider.Set(new StorageCell(TestItem.AddressA, 200), new byte[] { 2 });
        pathStateProvider.Set(new StorageCell(TestItem.AddressA, 300), new byte[] { 3 });

        pathStateProvider.Commit(MuirGlacier.Instance);
        pathStateProvider.CommitTree(1);

        pathStateProvider.ClearStorage(TestItem.AddressA);
        pathStateProvider.DeleteAccount(TestItem.AddressA);

        pathStateProvider.Commit(MuirGlacier.Instance);
        pathStateProvider.CommitTree(2);

        pathStateProvider.ClearStorage(TestItem.AddressA);
        pathStateProvider.CreateAccount(TestItem.AddressA, 200);
        pathStateProvider.Set(new StorageCell(TestItem.AddressA, 100), new byte[] { 100 });

        pathStateProvider.Commit(MuirGlacier.Instance);
        pathStateProvider.CommitTree(3);

        pathStateProvider.Commit(MuirGlacier.Instance);
        pathStateProvider.CommitTree(4);

        var data = pathStateProvider.Get(new StorageCell(TestItem.AddressA, 100));
        Assert.That(data, Is.Not.Null);
        Assert.That(data[0], Is.EqualTo(100));
    }

    private class TestPathPersistanceStrategy : IPersistenceStrategy
    {
        private int _delay;
        private int _interval;
        private long? _lastPersistedBlockNumber;
        public long? LastPersistedBlockNumber { get => _lastPersistedBlockNumber; set => _lastPersistedBlockNumber = value; }

        public TestPathPersistanceStrategy(int delay, int interval)
        {
            _delay = delay;
            _interval = interval;
        }

        public bool ShouldPersist(long blockNumber)
        {
            return false;
        }

        public bool ShouldPersist(long currentBlockNumber, out long targetBlockNumber)
        {
            targetBlockNumber = -1;
            long distanceToPersisted = currentBlockNumber - ((_lastPersistedBlockNumber ?? 0) + _delay);

            if (distanceToPersisted > 0 && distanceToPersisted % _interval == 0)
            {
                targetBlockNumber = currentBlockNumber - _delay;
                return true;
            }
            return false;
        }
    }
}
