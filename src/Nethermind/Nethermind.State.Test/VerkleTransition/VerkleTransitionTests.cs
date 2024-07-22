// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.VerkleTransition;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.TreeStore;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture]
public class MerkleToVerkleTransitionTests
{
    private IDb _merkleCodeDb;
    private IDb _preImageDb;
    private IDbProvider _dbProvider;
    private ITrieStore _trieStore;
    private StateTree _merkleStateTree;
    private VerkleStateTree _verkleStateTree;
    private AccountTreeMigrator _migrator;
    private ILogManager _logManager = new NUnitLogManager(LogLevel.Warn);

    [TearDown]
    public void TearDown()
    {
        _merkleCodeDb.Dispose();
        _preImageDb.Dispose();
        _dbProvider.Dispose();
        _trieStore.Dispose();
    }


    [SetUp]
    public void Setup()
    {
        _merkleCodeDb = new MemDb();
        _preImageDb = new MemDb();

        // initialize merkle tree
        _trieStore = new TrieStore(_merkleCodeDb, _logManager);
        _merkleStateTree = new StateTree(_trieStore, _logManager);
        IStateReader _stateReader = new StateReader(_trieStore, _merkleCodeDb, _logManager);

        // initialize verkle tree
        _dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        var verkleStore = new VerkleTreeStore<VerkleSyncCache>(_dbProvider, _logManager);
        _verkleStateTree = new VerkleStateTree(verkleStore, _logManager);

        _migrator = new AccountTreeMigrator(_verkleStateTree, _stateReader, _preImageDb);
    }

    [Test]
    public void Sanity_check()
    {
        AccountStruct? account = _verkleStateTree.Get(TestItem.AddressA)!;
        account.Value.IsTotallyEmpty.Should().BeTrue();
    }

    [Test]
    public void Can_migrate_single_account()
    {
        Account account = new Account(1).WithChangedBalance(2);
        _merkleStateTree.Set(TestItem.AddressA, account);
        _merkleStateTree.Commit(0);
        _preImageDb.Set(Keccak.Compute(TestItem.AddressA.Bytes).Bytes, TestItem.AddressA.Bytes);

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        AccountStruct? migratedAccount = _verkleStateTree.Get(TestItem.AddressA);
        migratedAccount.Should().NotBeNull();
        migratedAccount.Value.IsTotallyEmpty.Should().BeFalse();
        migratedAccount.Value.Balance.Should().Be(account.Balance);
        migratedAccount.Value.Nonce.Should().Be(account.Nonce);
        migratedAccount.Value.CodeHash.Should().Be(account.CodeHash);
    }

    [Test]
    public void Can_migrate_multiple_accounts()
    {
        Account account1 = new Account(0).WithChangedBalance(1);
        Account account2 = new Account(1).WithChangedBalance(2);
        _merkleStateTree.Set(TestItem.AddressA, account1);
        _merkleStateTree.Set(TestItem.AddressB, account2);
        _merkleStateTree.Commit(0);
        _preImageDb.Set(Keccak.Compute(TestItem.AddressA.Bytes).Bytes, TestItem.AddressA.Bytes);
        _preImageDb.Set(Keccak.Compute(TestItem.AddressB.Bytes).Bytes, TestItem.AddressB.Bytes);

        _preImageDb.GetAll().ForEach(kv => Console.WriteLine($"preimage db key: {kv.Key.ToHexString()} value: {kv.Value.ToHexString()}"));

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        AccountStruct? migratedAccount1 = _verkleStateTree.Get(TestItem.AddressA);
        AccountStruct? migratedAccount2 = _verkleStateTree.Get(TestItem.AddressB);

        migratedAccount1.Should().NotBeNull();
        migratedAccount1.Value.Balance.Should().Be(account1.Balance);

        migratedAccount2.Should().NotBeNull();
        migratedAccount2.Value.Balance.Should().Be(account2.Balance);
    }

    [Test]
    public void Can_migrate_account_with_storage()
    {
        Account account = new(0);
        _merkleStateTree.Set(TestItem.AddressA, account);

        var storageTree = new StorageTree(_trieStore.GetTrieStore(TestItem.AddressA.ToAccountPath), Keccak.EmptyTreeHash, _logManager);
        byte[] storageA = [1, 2, 3];
        byte[] storageB = [4, 5, 6];
        storageTree.Set(1, storageA);
        storageTree.Set(2, storageB);
        storageTree.Commit(0);

        _preImageDb.Set(Keccak.Compute(TestItem.AddressA.Bytes).Bytes, TestItem.AddressA.Bytes);

        account = account.WithChangedStorageRoot(storageTree.RootHash);

        _merkleStateTree.Set(TestItem.AddressA, account);
        _merkleStateTree.Commit(0);

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        AccountStruct? migratedAccount = _verkleStateTree.Get(TestItem.AddressA);
        migratedAccount.Should().NotBeNull();

        var storageValue1 = _verkleStateTree.Get(TestItem.AddressA, 1, storageTree.RootHash);
        var storageValue2 = _verkleStateTree.Get(TestItem.AddressA, 2, storageTree.RootHash);

        storageValue1.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        storageValue2.Should().BeEquivalentTo(new byte[] { 4, 5, 6 });
    }

    [Test]
    public void Can_migrate_contract_account_with_code()
    {
        byte[] code = Bytes.FromHexString("e3a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf601");
        Account account = new Account(0).WithChangedBalance(1).WithChangedCodeHash(Keccak.Compute(code), code);

        _merkleStateTree.Set(TestItem.AddressA, account);
        _merkleStateTree.Commit(0);
        _preImageDb.Set(Keccak.Compute(TestItem.AddressA.Bytes).Bytes, TestItem.AddressA.Bytes);
        _merkleCodeDb.Set(Keccak.Compute(code).Bytes, code);

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        AccountStruct? migratedAccount = _verkleStateTree.Get(TestItem.AddressA);
        migratedAccount.Should().NotBeNull();
        migratedAccount.Value.CodeHash.Should().Be(account.CodeHash);
        // TODO: why is CodeSize not set?
        // migratedAccount.Value.CodeSize.Should().Be(account.CodeSize);
        migratedAccount.Value.HasCode.Should().BeTrue();

        byte[] migratedCode = _verkleStateTree.GetCode(TestItem.AddressA);

        CompareCode(code, migratedCode).Should().BeTrue();
    }

    [Test]
    public void Can_migrate_empty_tree()
    {
        _merkleStateTree.Commit(0);

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        _verkleStateTree.StateRoot.Should().Be(Hash256.Zero);
    }

    [Test]
    public void Can_migrate_and_verify_root_hash()
    {
        Account account1 = TestItem.GenerateRandomAccount();
        Account account2 = TestItem.GenerateRandomAccount();
        _merkleStateTree.Set(TestItem.AddressA, account1);
        _merkleStateTree.Set(TestItem.AddressB, account2);
        _merkleStateTree.Commit(0);
        _preImageDb.Set(Keccak.Compute(TestItem.AddressA.Bytes).Bytes, TestItem.AddressA.Bytes);
        _preImageDb.Set(Keccak.Compute(TestItem.AddressB.Bytes).Bytes, TestItem.AddressB.Bytes);

        Hash256 merkleRootBefore = _merkleStateTree.RootHash;

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        Hash256 verkleRootAfter = _verkleStateTree.StateRoot;

        verkleRootAfter.Should().NotBe(Hash256.Zero);
        verkleRootAfter.Should().NotBe(merkleRootBefore);

        // Verify that the accounts exist in the Verkle tree
        AccountStruct? migratedAccount1 = _verkleStateTree.Get(TestItem.AddressA);
        AccountStruct? migratedAccount2 = _verkleStateTree.Get(TestItem.AddressB);

        migratedAccount1.Should().NotBeNull();
        migratedAccount1.Value.Balance.Should().Be(account1.Balance);
        migratedAccount1.Value.Nonce.Should().Be(account1.Nonce);

        migratedAccount2.Should().NotBeNull();
        migratedAccount2.Value.Balance.Should().Be(account2.Balance);
        migratedAccount2.Value.Nonce.Should().Be(account2.Nonce);
    }

    private static bool CompareCode(byte[] original, byte[] retrieved)
    {
        int numChunks = original.Length / 31; // each chunk starts with with '0' byte
        for (int i = 0; i < numChunks; i++)
        {
            byte[] originalChunk = original.Skip(i * 31).Take(31).ToArray();
            byte[] retrievedChunk = retrieved.Skip(i * 32 + 1).Take(31).ToArray();
            if (!originalChunk.SequenceEqual(retrievedChunk))
            {
                return false;
            }
        }
        return true;
    }
}
