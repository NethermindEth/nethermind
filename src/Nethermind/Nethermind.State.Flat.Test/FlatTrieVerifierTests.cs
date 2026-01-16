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

/// <summary>
/// Tests for FlatTrieVerifier which handles both hashed mode (single-pass co-iteration)
/// and preimage mode (two-pass verification).
/// </summary>
[TestFixture(FlatLayout.Flat)]
[TestFixture(FlatLayout.PreimageFlat)]
public class FlatTrieVerifierTests(FlatLayout layout)
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
            : new RocksDbPersistence(_columnsDb);
    }

    [TearDown]
    public void TearDown()
    {
        _trieDb.Dispose();
        _columnsDb.Dispose();
    }

    private StateId GetCurrentState()
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        return reader.CurrentState;
    }

    private void WriteAccountToFlat(Address address, Account account, StateId toState)
    {
        StateId fromState = GetCurrentState();
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(fromState, toState, WriteFlags.DisableWAL);
        batch.SetAccount(address, account);
    }

    private void WriteAccountsToFlat((Address address, Account account)[] accounts, StateId toState)
    {
        StateId fromState = GetCurrentState();
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(fromState, toState, WriteFlags.DisableWAL);
        foreach ((Address address, Account account) in accounts)
        {
            batch.SetAccount(address, account);
        }
    }

    private void WriteStorageDirectToDb(Address address, UInt256 slot, byte[] value)
    {
        TestMemDb storageDb = (TestMemDb)_columnsDb.GetColumnDb(FlatDbColumns.Storage);

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
        addrHash.Bytes[..4].CopyTo(storageKey.AsSpan()[..4]);
        slotHash.Bytes.CopyTo(storageKey.AsSpan()[4..36]);
        addrHash.Bytes[4..20].CopyTo(storageKey.AsSpan()[36..52]);

        storageDb.Set(storageKey, ((ReadOnlySpan<byte>)value).WithoutLeadingZeros().ToArray());
    }

    private void CorruptAccountInFlat(Address address, Account corruptedAccount)
    {
        TestMemDb accountDb = (TestMemDb)_columnsDb.GetColumnDb(FlatDbColumns.Account);
        ValueHash256 addrKey = layout == FlatLayout.PreimageFlat
            ? CreatePreimageAddressKey(address)
            : ValueKeccak.Compute(address.Bytes);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(corruptedAccount);
        accountDb.Set(addrKey.BytesAsSpan[..20], stream.AsSpan().ToArray());
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

        foreach ((UInt256 slot, byte[] value) in slots)
        {
            storageTree.Set(slot, value);
        }
        storageTree.Commit();
        return storageTree;
    }

    [Test]
    public void Verify_EmptyState_Succeeds()
    {
        Hash256 stateRoot = Keccak.EmptyTreeHash;

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInFlat, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [Test]
    public void Verify_SingleAccount_Matches()
    {
        Address address = TestItem.AddressA;
        Account account = new Account(1, 100);

        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        StateId toState = new StateId(1, stateRoot);
        WriteAccountToFlat(address, account, toState);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInFlat, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [Test]
    public void Verify_MultipleAccounts_AllMatch()
    {
        Address addressA = TestItem.AddressA;
        Address addressB = TestItem.AddressB;
        Address addressC = TestItem.AddressC;

        Account accountA = new Account(1, 100);
        Account accountB = new Account(2, 200);
        Account accountC = new Account(3, 300);

        _stateTree.Set(addressA, accountA);
        _stateTree.Set(addressB, accountB);
        _stateTree.Set(addressC, accountC);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        StateId toState = new StateId(1, stateRoot);
        WriteAccountsToFlat([(addressA, accountA), (addressB, accountB), (addressC, accountC)], toState);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(3));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInFlat, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [TestCase(1UL, 100UL, 1UL, 200UL, Description = "Mismatched balance")]
    [TestCase(5UL, 100UL, 10UL, 100UL, Description = "Mismatched nonce")]
    public void Verify_MismatchedAccount_DetectsMismatch(ulong trieNonce, ulong trieBalance, ulong flatNonce, ulong flatBalance)
    {
        Address address = TestItem.AddressA;
        Account trieAccount = new Account(trieNonce, trieBalance);
        Account flatAccount = new Account(flatNonce, flatBalance);

        _stateTree.Set(address, trieAccount);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        StateId toState = new StateId(1, stateRoot);
        WriteAccountToFlat(address, trieAccount, toState);
        CorruptAccountInFlat(address, flatAccount);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(1));
    }

    [Test]
    public void Verify_AccountInTrieNotInFlat_DetectsMissingInFlat()
    {
        Address address = TestItem.AddressA;
        Account account = new Account(1, 100);

        // Add to trie but not to flat
        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MissingInFlat, Is.EqualTo(1));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [Test]
    public void Verify_AccountInFlatNotInTrie_DetectsMissingInTrie()
    {
        Address address = TestItem.AddressA;
        Account account = new Account(1, 100);

        // Empty trie
        Hash256 stateRoot = Keccak.EmptyTreeHash;

        // Add to flat only
        StateId toState = new StateId(1, stateRoot);
        WriteAccountToFlat(address, account, toState);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MissingInFlat, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(1));
    }

    [Test]
    public void Verify_FlatHasExtraAccounts_ReportsMissing()
    {
        // Trie has 2 accounts, flat has 3 (1 extra)
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

        StateId toState = new StateId(1, stateRoot);
        WriteAccountsToFlat([(addressA, accountA), (addressB, accountB), (addressExtra, accountExtra)], toState);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(3));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(1));
    }

    [Test]
    public void Verify_Storage_AllMatch()
    {
        Address address = TestItem.AddressA;
        StorageTree storageTree = CreateStorageTree(address, [((UInt256)1, [0x11]), ((UInt256)2, [0x22])]);
        Account account = new Account(1, 100, storageTree.RootHash, Keccak.Compute([1]));

        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        StateId toState = new StateId(1, stateRoot);
        WriteAccountToFlat(address, account, toState);
        WriteStorageDirectToDb(address, 1, [0x11]);
        WriteStorageDirectToDb(address, 2, [0x22]);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.SlotCount, Is.EqualTo(2));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MismatchedSlot, Is.EqualTo(0));
    }

    [Test]
    public void Verify_Storage_Mismatch()
    {
        Address address = TestItem.AddressA;
        StorageTree storageTree = CreateStorageTree(address, [((UInt256)1, [0x11])]);
        Account account = new Account(1, 100, storageTree.RootHash, Keccak.Compute([1]));

        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        StateId toState = new StateId(1, stateRoot);
        WriteAccountToFlat(address, account, toState);
        WriteStorageDirectToDb(address, 1, [0xFF]); // Wrong value

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.SlotCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MismatchedSlot, Is.EqualTo(1));
    }

    [Test]
    public void Verify_MixedScenario_DetectsAllIssues()
    {
        // Account A: in both, matches
        Address addressA = TestItem.AddressA;
        Account accountA = new Account(1, 100);

        // Account B: in trie only (missing in flat)
        Address addressB = TestItem.AddressB;
        Account accountB = new Account(2, 200);

        // Account C: mismatched
        Address addressC = TestItem.AddressC;
        Account trieAccountC = new Account(3, 300);
        Account flatAccountC = new Account(3, 999);

        _stateTree.Set(addressA, accountA);
        _stateTree.Set(addressB, accountB);
        _stateTree.Set(addressC, trieAccountC);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        StateId toState = new StateId(1, stateRoot);
        WriteAccountsToFlat([(addressA, accountA), (addressC, trieAccountC)], toState);
        // Note: addressB not added to flat
        CorruptAccountInFlat(addressC, flatAccountC);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        FlatTrieVerifier verifier = new FlatTrieVerifier(_logManager);
        verifier.Verify(reader, _trieStore, stateRoot, CancellationToken.None);

        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(3));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(1)); // Account C mismatched
        Assert.That(verifier.Stats.MissingInFlat, Is.EqualTo(1)); // Account B missing in flat
    }
}
