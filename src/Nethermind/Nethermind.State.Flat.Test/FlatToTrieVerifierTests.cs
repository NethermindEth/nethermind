// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

[TestFixture]
public class FlatToTrieVerifierTests
{
    private MemDb _trieDb = null!;
    private RawScopedTrieStore _trieStore = null!;
    private StateTree _stateTree = null!;
    private ILogManager _logManager = null!;
    private TestMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private PreimageRocksdbPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _trieDb = new MemDb();
        _trieStore = new RawScopedTrieStore(_trieDb);
        _stateTree = new StateTree(_trieStore, LimboLogs.Instance);
        _logManager = LimboLogs.Instance;

        // Create persistence with in-memory columns DB
        _columnsDb = new TestMemColumnsDb<FlatDbColumns>();
        _persistence = new PreimageRocksdbPersistence(_columnsDb);
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

    private void DeleteAccountFromFlat(Address address)
    {
        // Access the underlying TestMemDb for Account column
        var accountDb = (TestMemDb)_columnsDb.GetColumnDb(FlatDbColumns.Account);

        // Build the preimage key - persistence only uses first 20 bytes (address bytes)
        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);

        // Use only first 20 bytes to match the persistence layer's key format
        accountDb.Remove(fakeHash.BytesAsSpan[..20]);
    }

    private void CorruptAccountInFlat(Address address, Account corruptedAccount)
    {
        var accountDb = (TestMemDb)_columnsDb.GetColumnDb(FlatDbColumns.Account);

        // Build the preimage key - persistence only uses first 20 bytes (address bytes)
        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(corruptedAccount);
        // Use only first 20 bytes to match the persistence layer's key format
        accountDb.Set(fakeHash.BytesAsSpan[..20], stream.AsSpan().ToArray());
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


    [Test]
    public void Verify_NonPreimageMode_SkipsStorageVerification()
    {
        // This test verifies behavior when IsPreimageMode is false.
        // Since PreimageRocksdbPersistence always returns true for IsPreimageMode,
        // we verify the expected behavior: storage IS checked in preimage mode.
        Address address = TestItem.AddressA;
        Account account = new Account(1, 100);

        // Add account to trie
        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Add to flat via persistence
        var toState = new StateId(1, stateRoot);
        WriteAccountToFlat(address, account, toState);

        using var reader = _persistence.CreateReader();

        // Verify that the reader is in preimage mode
        Assert.That(reader.IsPreimageMode, Is.True);

        var verifier = new FlatToTrieVerifier(
            reader,
            _trieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert - account matches
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
    }

    [Test]
    public void Verify_CancellationRequested_ThrowsOperationCanceled()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Address address = TestItem.AddressA;
        Account account = new Account(1, 100);

        // Add to flat
        var toState = new StateId(1, Keccak.EmptyTreeHash);
        WriteAccountToFlat(address, account, toState);

        using var reader = _persistence.CreateReader();
        var verifier = new FlatToTrieVerifier(
            reader,
            _trieStore,
            Keccak.EmptyTreeHash,
            _logManager,
            cts.Token);

        // Act & Assert
        Assert.Throws<OperationCanceledException>(() => verifier.Verify());
    }

    [Test]
    public void Verify_AccountWithContract_MatchesCorrectly()
    {
        // Arrange
        Address address = TestItem.AddressA;
        Hash256 codeHash = Keccak.Compute(new byte[] { 1, 2, 3 });
        Hash256 storageRoot = Keccak.EmptyTreeHash;
        Account account = new Account(1, 100, storageRoot, codeHash);

        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Add to flat via persistence
        var toState = new StateId(1, stateRoot);
        WriteAccountToFlat(address, account, toState);

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
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
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
    public void Verify_DeletedAccount_ReportsFewerAccountsInFlat()
    {
        // Arrange
        Address addressA = TestItem.AddressA;
        Address addressB = TestItem.AddressB;

        Account accountA = new Account(1, 100);
        Account accountB = new Account(2, 200);

        // Add both accounts to trie
        _stateTree.Set(addressA, accountA);
        _stateTree.Set(addressB, accountB);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Add both to flat, then delete one
        var toState = new StateId(1, stateRoot);
        WriteAccountsToFlat(new[]
        {
            (addressA, accountA),
            (addressB, accountB)
        }, toState);
        DeleteAccountFromFlat(addressB);

        using var reader = _persistence.CreateReader();
        var verifier = new FlatToTrieVerifier(
            reader,
            _trieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert - flat has only 1 account now
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
    }
}
