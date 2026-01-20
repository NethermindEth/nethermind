// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatToTrieVerifierTests
{
    private MemDb _trieDb = null!;
    private RawScopedTrieStore _trieStore = null!;
    private StateTree _stateTree = null!;
    private ILogManager _logManager = null!;

    [SetUp]
    public void SetUp()
    {
        _trieDb = new MemDb();
        _trieStore = new RawScopedTrieStore(_trieDb);
        _stateTree = new StateTree(_trieStore, LimboLogs.Instance);
        _logManager = LimboLogs.Instance;
    }

    [TearDown]
    public void TearDown()
    {
        _trieDb.Dispose();
    }

    private IPersistence.IPersistenceReader CreateMockReader(
        Dictionary<ValueHash256, byte[]> accounts,
        Dictionary<(ValueHash256, ValueHash256), byte[]> storage,
        bool isPreimageMode = true)
    {
        var reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.IsPreimageMode.Returns(isPreimageMode);

        // Setup account iterator
        var accountIterator = new TestFlatIterator(accounts);
        reader.CreateAccountIterator().Returns(_ => new TestFlatIterator(accounts));

        // Setup storage iterators
        reader.CreateStorageIterator(Arg.Any<ValueHash256>()).Returns(callInfo =>
        {
            var accountKey = callInfo.Arg<ValueHash256>();
            var filteredStorage = storage
                .Where(kv => kv.Key.Item1.Equals(accountKey))
                .ToDictionary(kv => kv.Key.Item2, kv => kv.Value);
            return new TestStorageIterator(filteredStorage);
        });

        // Setup GetAccountRaw for storage verification
        reader.GetAccountRaw(Arg.Any<Hash256>()).Returns(callInfo =>
        {
            var hash = callInfo.Arg<Hash256>();
            if (accounts.TryGetValue(hash.ValueHash256, out var data))
            {
                return data;
            }
            return null;
        });

        return reader;
    }

    private class TestFlatIterator : IPersistence.IFlatIterator
    {
        private readonly List<KeyValuePair<ValueHash256, byte[]>> _data;
        private int _index = -1;

        public TestFlatIterator(Dictionary<ValueHash256, byte[]> data)
        {
            _data = data.ToList();
        }

        public bool MoveNext()
        {
            _index++;
            return _index < _data.Count;
        }

        public ValueHash256 CurrentKey => _data[_index].Key;
        public ReadOnlySpan<byte> CurrentValue => _data[_index].Value;

        public void Dispose() { }
    }

    private class TestStorageIterator : IPersistence.IFlatIterator
    {
        private readonly List<KeyValuePair<ValueHash256, byte[]>> _data;
        private int _index = -1;

        public TestStorageIterator(Dictionary<ValueHash256, byte[]> data)
        {
            _data = data.ToList();
        }

        public bool MoveNext()
        {
            _index++;
            return _index < _data.Count;
        }

        public ValueHash256 CurrentKey => _data[_index].Key;
        public ReadOnlySpan<byte> CurrentValue => _data[_index].Value;

        public void Dispose() { }
    }

    [Test]
    public void Verify_EmptyState_Succeeds()
    {
        // Arrange
        var accounts = new Dictionary<ValueHash256, byte[]>();
        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
            Keccak.EmptyTreeHash,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [Test]
    public void Verify_SingleMatchingAccount_Succeeds()
    {
        // Arrange
        Address address = TestItem.AddressA;
        Account account = new Account(1, 100);

        // Add account to trie
        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Create flat data with matching account (using slim encoding)
        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(account);
        var accounts = new Dictionary<ValueHash256, byte[]>
        {
            { fakeHash, stream.AsSpan().ToArray() }
        };
        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [Test]
    public void Verify_MismatchedAccountBalance_DetectsMismatch()
    {
        // Arrange
        Address address = TestItem.AddressA;
        Account trieAccount = new Account(1, 100);
        Account flatAccount = new Account(1, 200); // Different balance

        // Add account to trie
        _stateTree.Set(address, trieAccount);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Create flat data with different account
        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(flatAccount);
        var accounts = new Dictionary<ValueHash256, byte[]>
        {
            { fakeHash, stream.AsSpan().ToArray() }
        };
        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
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

        // Create flat data with account
        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(flatAccount);
        var accounts = new Dictionary<ValueHash256, byte[]>
        {
            { fakeHash, stream.AsSpan().ToArray() }
        };
        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
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

        // Create flat data with matching accounts
        var accounts = new Dictionary<ValueHash256, byte[]>();
        foreach (var (address, account) in new[] { (addressA, accountA), (addressB, accountB), (addressC, accountC) })
        {
            ValueHash256 fakeHash = ValueKeccak.Zero;
            address.Bytes.CopyTo(fakeHash.BytesAsSpan);
            using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(account);
            accounts[fakeHash] = stream.AsSpan().ToArray();
        }

        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
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
    public void Verify_MultipleAccounts_OneMismatch()
    {
        // Arrange
        Address addressA = TestItem.AddressA;
        Address addressB = TestItem.AddressB;

        Account accountA = new Account(1, 100);
        Account trieAccountB = new Account(2, 200);
        Account flatAccountB = new Account(2, 999); // Different balance

        // Add accounts to trie
        _stateTree.Set(addressA, accountA);
        _stateTree.Set(addressB, trieAccountB);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Create flat data - A matches, B doesn't
        var accounts = new Dictionary<ValueHash256, byte[]>();

        ValueHash256 fakeHashA = ValueKeccak.Zero;
        addressA.Bytes.CopyTo(fakeHashA.BytesAsSpan);
        using var streamA = AccountDecoder.Slim.EncodeToNewNettyStream(accountA);
        accounts[fakeHashA] = streamA.AsSpan().ToArray();

        ValueHash256 fakeHashB = ValueKeccak.Zero;
        addressB.Bytes.CopyTo(fakeHashB.BytesAsSpan);
        using var streamB = AccountDecoder.Slim.EncodeToNewNettyStream(flatAccountB);
        accounts[fakeHashB] = streamB.AsSpan().ToArray();

        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(2));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(1));
    }

    [Test]
    public void Verify_NonPreimageMode_SkipsStorageVerification()
    {
        // Arrange
        Address address = TestItem.AddressA;
        Account account = new Account(1, 100);

        // Add account to trie
        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        // Create flat data
        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(account);
        var accounts = new Dictionary<ValueHash256, byte[]>
        {
            { fakeHash, stream.AsSpan().ToArray() }
        };

        // Add storage that would cause mismatch if checked
        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>
        {
            { (fakeHash, ValueKeccak.Compute(new byte[] { 1 })), new byte[] { 1, 2, 3 } }
        };

        // Non-preimage mode
        var reader = CreateMockReader(accounts, storage, isPreimageMode: false);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert - storage should not be checked in non-preimage mode
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.SlotCount, Is.EqualTo(0)); // Storage skipped
        Assert.That(verifier.Stats.MismatchedSlot, Is.EqualTo(0));
    }

    [Test]
    public void Verify_CancellationRequested_ThrowsOperationCanceled()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Address address = TestItem.AddressA;
        Account account = new Account(1, 100);

        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(account);
        var accounts = new Dictionary<ValueHash256, byte[]>
        {
            { fakeHash, stream.AsSpan().ToArray() }
        };
        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
            Keccak.EmptyTreeHash,
            _logManager,
            cts.Token);

        // Act & Assert
        Assert.Throws<OperationCanceledException>(() => verifier.Verify());
    }

    [Test]
    public void Stats_ToString_ReturnsFormattedString()
    {
        // Arrange
        var stats = new FlatToTrieVerifier.VerificationStats();

        // Act
        string result = stats.ToString();

        // Assert
        Assert.That(result, Does.Contain("FlatToTrie Stats"));
        Assert.That(result, Does.Contain("Accounts="));
        Assert.That(result, Does.Contain("Slots="));
    }

    [Test]
    public void Verify_AccountWithDifferentNonce_DetectsMismatch()
    {
        // Arrange
        Address address = TestItem.AddressA;
        Account trieAccount = new Account(5, 100); // Nonce = 5
        Account flatAccount = new Account(10, 100); // Different nonce

        _stateTree.Set(address, trieAccount);
        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(flatAccount);
        var accounts = new Dictionary<ValueHash256, byte[]>
        {
            { fakeHash, stream.AsSpan().ToArray() }
        };
        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(1));
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

        ValueHash256 fakeHash = ValueKeccak.Zero;
        address.Bytes.CopyTo(fakeHash.BytesAsSpan);

        using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(account);
        var accounts = new Dictionary<ValueHash256, byte[]>
        {
            { fakeHash, stream.AsSpan().ToArray() }
        };
        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(1));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
    }

    [Test]
    public void Verify_LargeNumberOfAccounts_ProcessesAll()
    {
        // Arrange
        int accountCount = 100;
        var addresses = new List<Address>();
        var accounts = new Dictionary<ValueHash256, byte[]>();

        for (int i = 0; i < accountCount; i++)
        {
            byte[] addressBytes = new byte[20];
            addressBytes[0] = (byte)(i / 256);
            addressBytes[1] = (byte)(i % 256);
            Address address = new Address(addressBytes);
            addresses.Add(address);

            Account account = new Account((UInt256)i, (UInt256)(i * 100));
            _stateTree.Set(address, account);

            ValueHash256 fakeHash = ValueKeccak.Zero;
            address.Bytes.CopyTo(fakeHash.BytesAsSpan);
            using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(account);
            accounts[fakeHash] = stream.AsSpan().ToArray();
        }

        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(accountCount));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(0));
        Assert.That(verifier.Stats.MissingInTrie, Is.EqualTo(0));
    }

    [Test]
    public void Verify_PartialMismatch_ReportsCorrectCounts()
    {
        // Arrange: 5 accounts, 2 mismatched
        var addresses = new Address[]
        {
            TestItem.AddressA,
            TestItem.AddressB,
            TestItem.AddressC,
            TestItem.AddressD,
            TestItem.AddressE
        };

        var accounts = new Dictionary<ValueHash256, byte[]>();

        for (int i = 0; i < addresses.Length; i++)
        {
            Address address = addresses[i];
            Account trieAccount = new Account((UInt256)i, (UInt256)(i * 100));
            _stateTree.Set(address, trieAccount);

            // Make accounts 1 and 3 mismatched
            Account flatAccount = (i == 1 || i == 3)
                ? new Account((UInt256)i, (UInt256)(i * 100 + 999))  // Different balance
                : trieAccount;

            ValueHash256 fakeHash = ValueKeccak.Zero;
            address.Bytes.CopyTo(fakeHash.BytesAsSpan);
            using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(flatAccount);
            accounts[fakeHash] = stream.AsSpan().ToArray();
        }

        _stateTree.Commit();
        Hash256 stateRoot = _stateTree.RootHash;

        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
            stateRoot,
            _logManager,
            CancellationToken.None);

        // Act
        verifier.Verify();

        // Assert
        Assert.That(verifier.Stats.AccountCount, Is.EqualTo(5));
        Assert.That(verifier.Stats.MismatchedAccount, Is.EqualTo(2));
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

        var accounts = new Dictionary<ValueHash256, byte[]>();

        foreach (var (address, account) in new[] { (addressA, accountA), (addressB, accountB), (addressExtra, accountExtra) })
        {
            ValueHash256 fakeHash = ValueKeccak.Zero;
            address.Bytes.CopyTo(fakeHash.BytesAsSpan);
            using var stream = AccountDecoder.Slim.EncodeToNewNettyStream(account);
            accounts[fakeHash] = stream.AsSpan().ToArray();
        }

        var storage = new Dictionary<(ValueHash256, ValueHash256), byte[]>();
        var reader = CreateMockReader(accounts, storage);

        var scopedTrieStore = _trieStore;
        var verifier = new FlatToTrieVerifier(
            reader,
            scopedTrieStore,
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
}
