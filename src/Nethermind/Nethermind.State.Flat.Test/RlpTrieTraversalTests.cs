// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Correctness tests for <see cref="RlpTrieTraversal.TryRead"/>: verifies that the raw leaf bytes
/// returned by <c>TryRead</c> match what <c>PatriciaTree.Get</c> / <c>StateTree.Get</c> returns.
/// </summary>
[TestFixture]
public class RlpTrieTraversalTests
{
    private MemDb _trieDb = null!;
    private RawScopedTrieStore _trieStore = null!;
    private StateTree _stateTree = null!;
    private RefCountingRlpNodePoolTracker _tracker = null!;

    [SetUp]
    public void SetUp()
    {
        _trieDb = new MemDb();
        _trieStore = new RawScopedTrieStore(_trieDb);
        _stateTree = new StateTree(_trieStore, LimboLogs.Instance);
        _tracker = new RefCountingRlpNodePoolTracker(new RefCountingTrieNodePool());
    }

    [TearDown]
    public void TearDown()
    {
        _trieDb.Dispose();
    }

    /// <summary>
    /// Builds a NodeLoader that loads RLP from the state trie store, wraps it in a RefCountingTrieNode.
    /// </summary>
    private NodeLoader MakeStateLoader() => (TreePath path, in ValueHash256 hash) =>
    {
        Hash256 h = hash.ToCommitment();
        byte[]? data = _trieStore.TryLoadRlp(path, h);
        if (data is null || data.Length > TrieNodeRlp.MaxRlpLength) return null;
        Assert.That(Keccak.Compute(data), Is.EqualTo(h), $"RLP hash mismatch at path {path}");
        return _tracker.Rent(hash, data);
    };

    /// <summary>
    /// Builds a NodeLoader for storage trie nodes.
    /// </summary>
    private NodeLoader MakeStorageLoader(Hash256 addressHash)
    {
        IScopedTrieStore storageStore = (IScopedTrieStore)_trieStore.GetStorageTrieNodeResolver(addressHash);
        return (TreePath path, in ValueHash256 hash) =>
        {
            Hash256 h = hash.ToCommitment();
            byte[]? data = storageStore.TryLoadRlp(path, h);
            if (data is null || data.Length > TrieNodeRlp.MaxRlpLength) return null;
            Assert.That(Keccak.Compute(data), Is.EqualTo(h), $"RLP hash mismatch at path {path}");
            return _tracker.Rent(hash, data);
        };
    }

    private StorageTree CreateStorageTree(Address address, (UInt256 slot, byte[] value)[] slots)
    {
        Hash256 addressHash = Keccak.Compute(address.Bytes);
        IScopedTrieStore storageTrieStore = (IScopedTrieStore)_trieStore.GetStorageTrieNodeResolver(addressHash);
        StorageTree storageTree = new StorageTree(storageTrieStore, LimboLogs.Instance);

        foreach ((UInt256 slot, byte[] value) in slots)
        {
            storageTree.Set(slot, value);
        }
        storageTree.Commit();
        return storageTree;
    }

    private static Address[] GenerateAddresses(int count)
    {
        Address[] addresses = new Address[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[20];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            addresses[i] = new Address(key);
        }
        return addresses;
    }

    // ------- State trie tests -------

    [Test]
    public void TryRead_EmptyTrie_ReturnsFalse()
    {
        bool found = RlpTrieTraversal.TryRead(MakeStateLoader(), Keccak.EmptyTreeHash,
            KeccakCache.Compute(TestItem.AddressA.Bytes).Bytes, out byte[]? value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void TryRead_SingleAccount_MatchesStateTreeGet()
    {
        Address address = TestItem.AddressA;
        Account account = new Account(1, 100);

        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        bool found = RlpTrieTraversal.TryRead(MakeStateLoader(), rootHash,
            KeccakCache.Compute(address.Bytes).Bytes, out byte[]? rawValue);

        Assert.That(found, Is.True);
        Assert.That(rawValue, Is.Not.Null);

        Account? fromTryRead = new AccountDecoder().Decode((ReadOnlySpan<byte>)rawValue!);
        Account? fromStateTree = _stateTree.Get(address, rootHash);

        Assert.That(fromTryRead!.Nonce, Is.EqualTo(fromStateTree!.Nonce));
        Assert.That(fromTryRead.Balance, Is.EqualTo(fromStateTree.Balance));
        Assert.That(fromTryRead.StorageRoot, Is.EqualTo(fromStateTree.StorageRoot));
        Assert.That(fromTryRead.CodeHash, Is.EqualTo(fromStateTree.CodeHash));
    }

    [TestCase(300)]
    [TestCase(50)]
    public void TryRead_MultipleAccounts_AllMatchStateTreeGet(int count)
    {
        Address[] addresses = GenerateAddresses(count);
        for (int i = 0; i < addresses.Length; i++)
        {
            _stateTree.Set(addresses[i], new Account((ulong)(i + 1), (UInt256)(i + 1) * 100));
        }
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        AccountDecoder decoder = new AccountDecoder();
        NodeLoader loader = MakeStateLoader();

        foreach (Address address in addresses)
        {
            bool found = RlpTrieTraversal.TryRead(loader, rootHash,
                KeccakCache.Compute(address.Bytes).Bytes, out byte[]? rawValue);

            Assert.That(found, Is.True, $"TryRead not found for {address}");

            Account? fromTryRead = decoder.Decode((ReadOnlySpan<byte>)rawValue!);
            Account? fromStateTree = _stateTree.Get(address, rootHash);

            Assert.That(fromTryRead!.Nonce, Is.EqualTo(fromStateTree!.Nonce), $"Nonce mismatch for {address}");
            Assert.That(fromTryRead.Balance, Is.EqualTo(fromStateTree.Balance), $"Balance mismatch for {address}");
        }
    }

    [Test]
    public void TryRead_MissingKey_ReturnsFalse()
    {
        _stateTree.Set(TestItem.AddressA, new Account(1, 100));
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        bool found = RlpTrieTraversal.TryRead(MakeStateLoader(), rootHash,
            KeccakCache.Compute(TestItem.AddressB.Bytes).Bytes, out byte[]? value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void WarmUpPath_DoesNotThrow_AndCompletes()
    {
        _stateTree.Set(TestItem.AddressA, new Account(1, 100));
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        Assert.DoesNotThrow(() =>
            RlpTrieTraversal.WarmUpPath(MakeStateLoader(), rootHash,
                KeccakCache.Compute(TestItem.AddressA.Bytes).Bytes));
    }

    // ------- Storage trie tests -------

    [Test]
    public void TryRead_SingleStorageSlot_MatchesStorageTreeGet()
    {
        Address address = TestItem.AddressA;
        UInt256 slot = 1;
        byte[] slotValue = [0x11];

        StorageTree storageTree = CreateStorageTree(address, [(slot, slotValue)]);
        Hash256 rootHash = storageTree.RootHash;
        Hash256 addressHash = Keccak.Compute(address.Bytes);
        NodeLoader storageLoader = MakeStorageLoader(addressHash);

        ValueHash256 key = default;
        StorageTree.ComputeKeyWithLookup(slot, ref key);

        bool found = RlpTrieTraversal.TryRead(storageLoader, rootHash, key.Bytes, out byte[]? rawValue);

        Assert.That(found, Is.True);
        Assert.That(rawValue, Is.Not.Null);

        Rlp.ValueDecoderContext rlpCtx = rawValue!.AsRlpValueContext();
        byte[] decodedValue = rlpCtx.DecodeByteArray();
        byte[] expected = storageTree.Get(slot);

        Assert.That(decodedValue, Is.EqualTo(expected));
    }

    [TestCase(300)]
    [TestCase(50)]
    public void TryRead_MultipleStorageSlots_AllMatchStorageTreeGet(int count)
    {
        Address address = TestItem.AddressA;
        (UInt256 slot, byte[] value)[] slots = new (UInt256, byte[])[count];
        for (int i = 0; i < count; i++)
        {
            slots[i] = ((UInt256)(i + 1), [(byte)((i % 255) + 1)]);
        }

        StorageTree storageTree = CreateStorageTree(address, slots);
        Hash256 rootHash = storageTree.RootHash;
        Hash256 addressHash = Keccak.Compute(address.Bytes);
        NodeLoader storageLoader = MakeStorageLoader(addressHash);

        foreach ((UInt256 slot, _) in slots)
        {
            ValueHash256 key = default;
            StorageTree.ComputeKeyWithLookup(slot, ref key);

            bool found = RlpTrieTraversal.TryRead(storageLoader, rootHash, key.Bytes, out byte[]? rawValue);

            Assert.That(found, Is.True, $"TryRead not found for slot {slot}");

            Rlp.ValueDecoderContext rlpCtx = rawValue!.AsRlpValueContext();
            byte[] decodedValue = rlpCtx.DecodeByteArray();
            byte[] expected = storageTree.Get(slot);
            Assert.That(decodedValue, Is.EqualTo(expected), $"Value mismatch for slot {slot}");
        }
    }

    [Test]
    public void TryRead_MissingStorageSlot_ReturnsFalse()
    {
        Address address = TestItem.AddressA;
        StorageTree storageTree = CreateStorageTree(address, [((UInt256)1, [0x11])]);
        Hash256 rootHash = storageTree.RootHash;
        Hash256 addressHash = Keccak.Compute(address.Bytes);
        NodeLoader storageLoader = MakeStorageLoader(addressHash);

        ValueHash256 absentKey = default;
        StorageTree.ComputeKeyWithLookup((UInt256)2, ref absentKey);

        bool found = RlpTrieTraversal.TryRead(storageLoader, rootHash, absentKey.Bytes, out byte[]? value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.Null);
    }

    // ------- Inline node / small trie tests -------

    [Test]
    public void TryRead_SmallTrie_InlineNodesHandledCorrectly()
    {
        Address address = TestItem.AddressA;
        _stateTree.Set(address, new Account(42, 1000));
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        bool found = RlpTrieTraversal.TryRead(MakeStateLoader(), rootHash,
            KeccakCache.Compute(address.Bytes).Bytes, out byte[]? rawValue);

        Assert.That(found, Is.True);

        Account? decoded = new AccountDecoder().Decode((ReadOnlySpan<byte>)rawValue!);
        Assert.That(decoded!.Nonce, Is.EqualTo((UInt256)42));
        Assert.That(decoded.Balance, Is.EqualTo((UInt256)1000));
    }
}
