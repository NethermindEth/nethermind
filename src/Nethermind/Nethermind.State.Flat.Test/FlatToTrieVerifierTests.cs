// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture(FlatLayout.PreimageFlat)]
[TestFixture(FlatLayout.Flat)]
public class FlatToTrieVerifierTests(FlatLayout layout)
{
    private MemDb _trieDb = null!;
    private RawScopedTrieStore _trieStore = null!;
    private StateTree _stateTree = null!;
    private ILogManager _logManager = null!;
    private TestMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _trieDb = new MemDb();
        _trieStore = new RawScopedTrieStore(_trieDb);
        _stateTree = new StateTree(_trieStore, LimboLogs.Instance);
        _logManager = LimboLogs.Instance;

        _columnsDb = new TestMemColumnsDb<FlatDbColumns>();
        _persistence = layout == FlatLayout.PreimageFlat
            ? new PreimageRocksdbPersistence(_columnsDb)
            : new RocksdbPersistence(_columnsDb);
    }

    [TearDown]
    public void TearDown()
    {
        _trieDb.Dispose();
        _columnsDb.Dispose();
    }

    private StateId GetCurrentState()
    {
        using var reader = _persistence.CreateReader();
        return reader.CurrentState;
    }

    private void WriteAccountToFlat(Address address, Account account, StateId toState)
    {
        var fromState = GetCurrentState();
        using var batch = _persistence.CreateWriteBatch(fromState, toState, WriteFlags.DisableWAL);
        batch.SetAccount(address, account);
    }

    private void WriteAccountsToFlat(IEnumerable<(Address address, Account account)> accounts, StateId toState)
    {
        var fromState = GetCurrentState();
        using var batch = _persistence.CreateWriteBatch(fromState, toState, WriteFlags.DisableWAL);
        foreach (var (address, account) in accounts)
        {
            batch.SetAccount(address, account);
        }
    }

    private void CorruptAccountInFlat(Address address, Account corruptedAccount)
    {
        var accountDb = (TestMemDb)_columnsDb.GetColumnDb(FlatDbColumns.Account);
        ValueHash256 addrKey = layout == FlatLayout.PreimageFlat
            ? CreatePreimageAddressKey(address)
            : ValueKeccak.Compute(address.Bytes);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(corruptedAccount);
        accountDb.Set(addrKey.BytesAsSpan[..20], stream.AsSpan().ToArray());
    }

    private void WriteStorageDirectToDb(Address address, UInt256 slot, byte[] value)
    {
        var storageDb = (TestMemDb)_columnsDb.GetColumnDb(FlatDbColumns.Storage);

        ValueHash256 addrHash;
        ValueHash256 slotHash;

        if (layout == FlatLayout.PreimageFlat)
        {
            addrHash = CreatePreimageAddressKey(address);
            slotHash = ValueKeccak.Zero;
            slot.ToBigEndian(slotHash.BytesAsSpan);
        }
        else
        {
            addrHash = ValueKeccak.Compute(address.Bytes);
            Span<byte> slotBytes = stackalloc byte[32];
            slot.ToBigEndian(slotBytes);
            slotHash = ValueKeccak.Compute(slotBytes);
        }

        byte[] storageKey = new byte[52];
        addrHash.Bytes[..4].CopyTo(storageKey.AsSpan(0, 4));
        slotHash.Bytes.CopyTo(storageKey.AsSpan(4, 32));
        addrHash.Bytes[4..20].CopyTo(storageKey.AsSpan(36, 16));

        storageDb.Set(storageKey, value.AsSpan().WithoutLeadingZeros().ToArray());
    }

    private static ValueHash256 CreatePreimageAddressKey(Address address)
    {
        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);
        return fakeHash;
    }

    private StorageTree CreateStorageTree(Address address, (UInt256 slot, byte[] value)[] slots)
    {
        Hash256 addressHash = Keccak.Compute(address.Bytes);
        IScopedTrieStore storageTrieStore = (IScopedTrieStore)_trieStore.GetStorageTrieNodeResolver(addressHash);
        StorageTree storageTree = new StorageTree(storageTrieStore, _logManager);

        foreach (var (slot, value) in slots)
        {
            storageTree.Set(slot, value);
        }
        storageTree.Commit();
        return storageTree;
    }

    [Test]
    public void Verify_EmptyState_Succeeds()
    {
        // Arrange - empty state
        Hash256 stateRoot = Keccak.EmptyTreeHash;

        using var reader = _persistence.CreateReader();
        var verifier = new FlatToTrieVerifier(
            reader,
            _trieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [TestCase(1UL, 100UL, 1UL, 200UL, Description = "Mismatched balance")]
    [TestCase(5UL, 100UL, 10UL, 100UL, Description = "Mismatched nonce")]
    [TestCase(1UL, 100UL, 2UL, 200UL, Description = "Mismatched nonce and balance")]
    public void Verify_MismatchedAccount_DetectsMismatch(ulong trieNonce, ulong trieBalance, ulong flatNonce, ulong flatBalance)
    {
        // Arrange
        Address address = TestItem.AddressA;
        Account trieAccount = new Account(trieNonce, trieBalance);
        Account flatAccount = new Account(flatNonce, flatBalance);

        // Add account to trie
        _stateTree.Set(address, trieAccount);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Add correct account to flat, then corrupt it
        var toState = new StateId(1, stateRoot);
        WriteAccountToFlat(address, trieAccount, toState);
        CorruptAccountInFlat(address, flatAccount);

        using var reader = _persistence.CreateReader();
        var verifier = new FlatToTrieVerifier(
            reader,
            _trieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(1));
    }

    [Test]
    public void Verify_AccountInFlatNotInTrie_DetectsMissing()
    {
        // Arrange - empty trie but flat has an account
        Address address = TestItem.AddressA;
        Account flatAccount = new Account(1, 100);

        // Don't add to trie, use empty state root
        Hash256 stateRoot = Keccak.EmptyTreeHash;

        // Add to flat with fake state
        var toState = new StateId(1, stateRoot);
        WriteAccountToFlat(address, flatAccount, toState);

        using var reader = _persistence.CreateReader();
        var verifier = new FlatToTrieVerifier(
            reader,
            _trieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(1));
    }

    [Test]
    public void Verify_MultipleAccounts_AllMatch()
    {
        // Arrange
        Address addressA = TestItem.AddressA;
        Address addressB = TestItem.AddressB;
        Address addressC = TestItem.AddressC;

        Account accountA = new Account(1, 100);
        Account accountB = new Account(2, 200);
        Account accountC = new Account(3, 300);

        // Add accounts to trie
        _stateTree.Set(addressA, accountA);
        _stateTree.Set(addressB, accountB);
        _stateTree.Set(addressC, accountC);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Add to flat via persistence
        var toState = new StateId(1, stateRoot);
        WriteAccountsToFlat(new[]
        {
            (addressA, accountA),
            (addressB, accountB),
            (addressC, accountC)
        }, toState);

        using var reader = _persistence.CreateReader();
        var verifier = new FlatToTrieVerifier(
            reader,
            _trieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(3));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [TestCase(2, 1, Description = "2 accounts, 1 mismatched")]
    [TestCase(5, 2, Description = "5 accounts, 2 mismatched")]
    public void Verify_PartialMismatch_ReportsCorrectCounts(int totalAccounts, int mismatchedCount)
    {
        var addresses = new Address[]
        {
            TestItem.AddressA,
            TestItem.AddressB,
            TestItem.AddressC,
            TestItem.AddressD,
            TestItem.AddressE
        };

        var accountsToWrite = new List<(Address, Account)>();

        for (int i = 0; i < totalAccounts; i++)
        {
            Address address = addresses[i];
            Account trieAccount = new Account((UInt256)i, (UInt256)(i * 100));
            _stateTree.Set(address, trieAccount);
            accountsToWrite.Add((address, trieAccount));
        }

        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Add all correct accounts to flat
        var toState = new StateId(1, stateRoot);
        WriteAccountsToFlat(accountsToWrite, toState);

        // Corrupt the specified number of accounts (every other one starting at index 1)
        int corrupted = 0;
        for (int i = 1; i < totalAccounts && corrupted < mismatchedCount; i += 2)
        {
            Account corruptedAccount = new Account((UInt256)i, (UInt256)(i * 100 + 999));
            CorruptAccountInFlat(addresses[i], corruptedAccount);
            corrupted++;
        }

        using var reader = _persistence.CreateReader();
        var verifier = new FlatToTrieVerifier(
            reader,
            _trieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(totalAccounts));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(mismatchedCount));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [Test]
    public void Verify_FlatHasExtraAccounts_ReportsMissing()
    {
        // Arrange: Trie has 2 accounts, flat has 3 (1 extra)
        Address addressA = TestItem.AddressA;
        Address addressB = TestItem.AddressB;
        Address addressExtra = TestItem.AddressC;

        Account accountA = new Account(1, 100);
        Account accountB = new Account(2, 200);
        Account accountExtra = new Account(3, 300);

        _stateTree.Set(addressA, accountA);
        _stateTree.Set(addressB, accountB);
        // Note: addressExtra NOT added to trie
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Add all accounts to flat (including extra)
        var toState = new StateId(1, stateRoot);
        WriteAccountsToFlat(new[]
        {
            (addressA, accountA),
            (addressB, accountB),
            (addressExtra, accountExtra)
        }, toState);

        using var reader = _persistence.CreateReader();
        var verifier = new FlatToTrieVerifier(
            reader,
            _trieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(3));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(1)); // Extra account not in trie
    }

    [Test]
    public void Verify_Storage_MatchesAndMismatches()
    {
        // Account A: 2 matching storage slots
        Address addressA = TestItem.AddressA;
        StorageTree storageA = CreateStorageTree(addressA, [((UInt256)1, [0x11]), ((UInt256)2, [0x22])]);
        Account accountA = new Account(1, 100, storageA.RootHash, Keccak.Compute([1]));

        // Account B: 1 mismatched storage slot
        Address addressB = TestItem.AddressB;
        StorageTree storageB = CreateStorageTree(addressB, [((UInt256)10, [0xAA])]);
        Account accountB = new Account(2, 200, storageB.RootHash, Keccak.Compute([2]));

        _stateTree.Set(addressA, accountA);
        _stateTree.Set(addressB, accountB);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        var toState = new StateId(1, stateRoot);
        WriteAccountsToFlat([(addressA, accountA), (addressB, accountB)], toState);

        // Account A storage matches
        WriteStorageDirectToDb(addressA, 1, [0x11]);
        WriteStorageDirectToDb(addressA, 2, [0x22]);
        // Account B storage mismatches
        WriteStorageDirectToDb(addressB, 10, [0xFF]);

        using var reader = _persistence.CreateReader();
        var verifier = new FlatToTrieVerifier(reader, _trieStore, stateRoot, _logManager, CancellationToken.None);
        verifier.Verify();

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(2));
        Assert.That(verifier.Stats.SlotCount, Is.EqualTo(3));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MismatchedSlot, Is.EqualTo(1));
    }
}
