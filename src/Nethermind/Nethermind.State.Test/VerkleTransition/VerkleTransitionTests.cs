// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private IDb merkleDb;
    private IDb preImageDb;
    private IDbProvider _dbProvider;
    private ITrieStore _trieStore;
    private StateTree _merkleStateTree;
    private VerkleStateTree _verkleStateTree;
    private AccountTreeMigrator _migrator;
    private ILogManager _logManager = new NUnitLogManager(LogLevel.Warn);

    // TODO: remove this once we have implemented pre-image db logic in the AccountTreeMigrator
    private static Address GetActualAddress(
        byte[] bytes
    )
    {
        return new Address(Keccak.Compute(bytes));

    }

    [TearDown]
    public void TearDown()
    {
        merkleDb.Dispose();
        preImageDb.Dispose();
        _dbProvider.Dispose();
        _trieStore.Dispose();
    }


    [SetUp]
    public void Setup()
    {
        merkleDb = new MemDb();
        preImageDb = new MemDb();

        // initialize merkle tree
        _trieStore = new TrieStore(merkleDb, _logManager);
        _merkleStateTree = new StateTree(_trieStore, _logManager);
        IStateReader _stateReader = new StateReader(_trieStore, merkleDb, _logManager);

        // initialize verkle tree
        _dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        var verkleStore = new VerkleTreeStore<VerkleSyncCache>(_dbProvider, _logManager);
        _verkleStateTree = new VerkleStateTree(verkleStore, _logManager);

        _migrator = new AccountTreeMigrator(_verkleStateTree, _stateReader, preImageDb);
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
        Account account = new(1);
        _merkleStateTree.Set(TestItem.AddressA, account);
        _merkleStateTree.Commit(0);

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        Address address = GetActualAddress(TestItem.AddressA.Bytes);

        AccountStruct? migratedAccount = _verkleStateTree.Get(address);
        migratedAccount.Should().NotBeNull();
        migratedAccount.Value.IsTotallyEmpty.Should().BeFalse();
        migratedAccount.Value.Balance.Should().Be(account.Balance);
        migratedAccount.Value.Nonce.Should().Be(account.Nonce);
        migratedAccount.Value.CodeHash.Should().Be(account.CodeHash);
    }

    [Test]
    public void Can_migrate_multiple_accounts()
    {
        var account1 = new Account(0);
        var account2 = new Account(1);
        _merkleStateTree.Set(TestItem.AddressA, account1);
        _merkleStateTree.Set(TestItem.AddressB, account2);
        _merkleStateTree.Commit(0);

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        Address addressA = GetActualAddress(TestItem.AddressA.Bytes);
        Address addressB = GetActualAddress(TestItem.AddressB.Bytes);
        AccountStruct? migratedAccount1 = _verkleStateTree.Get(addressA);
        AccountStruct? migratedAccount2 = _verkleStateTree.Get(addressB);

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
        storageTree.Set(1, [1, 2, 3]);
        storageTree.Set(2, [4, 5, 6]);
        storageTree.Commit(0);

        account = account.WithChangedStorageRoot(storageTree.RootHash);

        _merkleStateTree.Set(TestItem.AddressA, account);
        _merkleStateTree.Commit(0);

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        Address address = GetActualAddress(TestItem.AddressA.Bytes);
        AccountStruct? migratedAccount = _verkleStateTree.Get(address);
        migratedAccount.Should().NotBeNull();

        var storageValue1 = _verkleStateTree.Get(TestItem.AddressA, 1);
        var storageValue2 = _verkleStateTree.Get(TestItem.AddressA, 2);

        storageValue1.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        storageValue2.Should().BeEquivalentTo(new byte[] { 4, 5, 6 });
    }

    [Test]
    public void Can_migrate_contract_account_with_code()
    {
        byte[] code = Bytes.FromHexString("e3a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf601");
        Account account = TestItem.GenerateRandomAccount().WithChangedCodeHash(Keccak.Compute(code), code);

        _merkleStateTree.Set(TestItem.AddressA, account);
        _merkleStateTree.Commit(0);

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        Address address = GetActualAddress(TestItem.AddressA.Bytes);
        AccountStruct? migratedAccount = _verkleStateTree.Get(address);
        migratedAccount.Should().NotBeNull();
        migratedAccount.Value.CodeHash.Should().Be(account.CodeHash);
        migratedAccount.Value.CodeSize.Should().Be(account.CodeSize);
        migratedAccount.Value.HasCode.Should().BeTrue();

        byte[] migratedCode = _verkleStateTree.GetCode(address);
        migratedCode.Should().BeEquivalentTo(code);
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

        Hash256 merkleRootBefore = _merkleStateTree.RootHash;

        _merkleStateTree.Accept(_migrator, _merkleStateTree.RootHash);
        _migrator.FinalizeMigration(0);

        Hash256 verkleRootAfter = _verkleStateTree.StateRoot;

        verkleRootAfter.Should().NotBe(Hash256.Zero);
        verkleRootAfter.Should().NotBe(merkleRootBefore);

        // Verify that the accounts exist in the Verkle tree
        Address addressA = GetActualAddress(TestItem.AddressA.Bytes);
        Address addressB = GetActualAddress(TestItem.AddressB.Bytes);

        AccountStruct? migratedAccount1 = _verkleStateTree.Get(addressA);
        AccountStruct? migratedAccount2 = _verkleStateTree.Get(addressB);

        migratedAccount1.Should().NotBeNull();
        migratedAccount1.Value.Balance.Should().Be(account1.Balance);
        migratedAccount1.Value.Nonce.Should().Be(account1.Nonce);

        migratedAccount2.Should().NotBeNull();
        migratedAccount2.Value.Balance.Should().Be(account2.Balance);
        migratedAccount2.Value.Nonce.Should().Be(account2.Nonce);
    }
}
