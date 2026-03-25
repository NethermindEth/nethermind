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

    [SetUp]
    public void SetUp()
    {
        _trieDb = new MemDb();
        _trieStore = new RawScopedTrieStore(_trieDb);
        _stateTree = new StateTree(_trieStore, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _trieDb.Dispose();
    }

    // Builds an RlpLoader that delegates to the state trie store.
    // Asserts that the loaded RLP actually hashes to the expected hash.
    private RlpLoader MakeStateLoader() => (TreePath path, Hash256 hash, ref TrieNodeRlp target) =>
    {
        byte[]? data = _trieStore.TryLoadRlp(path, hash);
        if (data is null) return false;
        if (data.Length > TrieNodeRlp.MaxRlpLength) throw new Exception($"RLP at path {path} exceeds MaxRlpLength: {data.Length} > {TrieNodeRlp.MaxRlpLength}");
        Assert.That(Keccak.Compute(data), Is.EqualTo(hash), $"RLP hash mismatch at path {path}");
        target.Set(data);
        return true;
    };

    // Builds an RlpLoader that delegates to the storage trie store for the given contract address hash.
    // Asserts that the loaded RLP actually hashes to the expected hash.
    private RlpLoader MakeStorageLoader(Hash256 addressHash)
    {
        IScopedTrieStore storageStore = (IScopedTrieStore)_trieStore.GetStorageTrieNodeResolver(addressHash);
        return (TreePath path, Hash256 hash, ref TrieNodeRlp target) =>
        {
            byte[]? data = storageStore.TryLoadRlp(path, hash);
            if (data is null) return false;
            if (data.Length > TrieNodeRlp.MaxRlpLength) throw new Exception($"RLP at path {path} exceeds MaxRlpLength: {data.Length} > {TrieNodeRlp.MaxRlpLength}");
            Assert.That(Keccak.Compute(data), Is.EqualTo(hash), $"RLP hash mismatch at path {path}");
            target.Set(data);
            return true;
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

        // Decode TryRead result and compare with StateTree.Get
        Account? fromTryRead = new AccountDecoder().Decode((ReadOnlySpan<byte>)rawValue!);
        Account? fromStateTree = _stateTree.Get(address, rootHash);

        Assert.That(fromStateTree, Is.Not.Null);
        Assert.That(fromTryRead, Is.Not.Null);
        Assert.That(fromTryRead!.Nonce, Is.EqualTo(fromStateTree!.Nonce));
        Assert.That(fromTryRead.Balance, Is.EqualTo(fromStateTree.Balance));
        Assert.That(fromTryRead.StorageRoot, Is.EqualTo(fromStateTree.StorageRoot));
        Assert.That(fromTryRead.CodeHash, Is.EqualTo(fromStateTree.CodeHash));
    }

    [Test]
    public void TryRead_MultipleAccounts_AllMatchStateTreeGet()
    {
        const int count = 300;
        Address[] addresses = new Address[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[20];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            addresses[i] = new Address(key);
        }

        for (int i = 0; i < addresses.Length; i++)
        {
            _stateTree.Set(addresses[i], new Account((ulong)(i + 1), (UInt256)(i + 1) * 100));
        }
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        AccountDecoder decoder = new AccountDecoder();
        RlpLoader loader = MakeStateLoader();

        foreach (Address address in addresses)
        {
            bool found = RlpTrieTraversal.TryRead(loader, rootHash,
                KeccakCache.Compute(address.Bytes).Bytes, out byte[]? rawValue);

            Assert.That(found, Is.True, $"TryRead not found for {address}");
            Assert.That(rawValue, Is.Not.Null);

            Account? fromTryRead = decoder.Decode((ReadOnlySpan<byte>)rawValue!);
            Account? fromStateTree = _stateTree.Get(address, rootHash);

            Assert.That(fromStateTree, Is.Not.Null);
            Assert.That(fromTryRead, Is.Not.Null);
            Assert.That(fromTryRead!.Nonce, Is.EqualTo(fromStateTree!.Nonce), $"Nonce mismatch for {address}");
            Assert.That(fromTryRead.Balance, Is.EqualTo(fromStateTree.Balance), $"Balance mismatch for {address}");
        }
    }

    [Test]
    public void TryRead_MissingKey_ReturnsFalse()
    {
        Address presentAddress = TestItem.AddressA;
        Address absentAddress = TestItem.AddressB;

        _stateTree.Set(presentAddress, new Account(1, 100));
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        bool found = RlpTrieTraversal.TryRead(MakeStateLoader(), rootHash,
            KeccakCache.Compute(absentAddress.Bytes).Bytes, out byte[]? value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void WarmUpPath_DoesNotThrow_AndCompletes()
    {
        Address address = TestItem.AddressA;
        _stateTree.Set(address, new Account(1, 100));
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        // Should not throw; just warms up the cache path
        Assert.DoesNotThrow(() =>
            RlpTrieTraversal.WarmUpPath(MakeStateLoader(), rootHash,
                KeccakCache.Compute(address.Bytes).Bytes));
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
        RlpLoader storageLoader = MakeStorageLoader(addressHash);

        ValueHash256 key = default;
        StorageTree.ComputeKeyWithLookup(slot, ref key);

        bool found = RlpTrieTraversal.TryRead(storageLoader, rootHash, key.Bytes, out byte[]? rawValue);

        Assert.That(found, Is.True);
        Assert.That(rawValue, Is.Not.Null);

        // StorageTree.GetArray does an extra DecodeByteArray on top of what TryRead returns
        Rlp.ValueDecoderContext rlpCtx = rawValue!.AsRlpValueContext();
        byte[] decodedValue = rlpCtx.DecodeByteArray();

        byte[] expected = storageTree.Get(slot);

        Assert.That(decodedValue, Is.EqualTo(expected));
    }

    [Test]
    public void TryRead_MultipleStorageSlots_AllMatchStorageTreeGet()
    {
        const int count = 300;
        Address address = TestItem.AddressA;
        (UInt256 slot, byte[] value)[] slots = new (UInt256, byte[])[count];
        for (int i = 0; i < count; i++)
        {
            slots[i] = ((UInt256)(i + 1), [(byte)((i % 255) + 1)]);
        }

        StorageTree storageTree = CreateStorageTree(address, slots);
        Hash256 rootHash = storageTree.RootHash;
        Hash256 addressHash = Keccak.Compute(address.Bytes);
        RlpLoader storageLoader = MakeStorageLoader(addressHash);

        foreach ((UInt256 slot, _) in slots)
        {
            ValueHash256 key = default;
            StorageTree.ComputeKeyWithLookup(slot, ref key);

            bool found = RlpTrieTraversal.TryRead(storageLoader, rootHash, key.Bytes, out byte[]? rawValue);

            Assert.That(found, Is.True, $"TryRead not found for slot {slot}");
            Assert.That(rawValue, Is.Not.Null);

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
        RlpLoader storageLoader = MakeStorageLoader(addressHash);

        // Slot 2 was never set
        ValueHash256 absentKey = default;
        StorageTree.ComputeKeyWithLookup((UInt256)2, ref absentKey);

        bool found = RlpTrieTraversal.TryRead(storageLoader, rootHash, absentKey.Bytes, out byte[]? value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.Null);
    }

    // ------- Extension node tests -------

    [Test]
    public void TryRead_ExtensionNode_TraversedCorrectly()
    {
        // Many accounts to ensure the trie has extension nodes at various depths.
        const int count = 300;
        Address[] addresses = new Address[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[20];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            addresses[i] = new Address(key);
        }

        for (int i = 0; i < addresses.Length; i++)
        {
            _stateTree.Set(addresses[i], new Account((ulong)(i + 1), (UInt256)(i + 1)));
        }
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        AccountDecoder decoder = new AccountDecoder();
        RlpLoader loader = MakeStateLoader();

        // Verify all round-trip correctly (including any extension nodes along the paths)
        foreach (Address address in addresses)
        {
            bool found = RlpTrieTraversal.TryRead(loader, rootHash,
                KeccakCache.Compute(address.Bytes).Bytes, out byte[]? rawValue);

            Assert.That(found, Is.True, $"TryRead not found for {address}");

            Account? fromTryRead = decoder.Decode((ReadOnlySpan<byte>)rawValue!);
            Account? fromStateTree = _stateTree.Get(address, rootHash);

            Assert.That(fromTryRead!.Balance, Is.EqualTo(fromStateTree!.Balance));
        }
    }

    // ------- Inline node tests -------

    [Test]
    public void TryRead_SmallTrie_InlineNodesHandledCorrectly()
    {
        // A trie with a single account may produce inline child nodes.
        // The traversal should handle these without hash-loading them.
        Address address = TestItem.AddressA;
        Account account = new Account(42, 1000);

        _stateTree.Set(address, account);
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        bool found = RlpTrieTraversal.TryRead(MakeStateLoader(), rootHash,
            KeccakCache.Compute(address.Bytes).Bytes, out byte[]? rawValue);

        Assert.That(found, Is.True);

        Account? decoded = new AccountDecoder().Decode((ReadOnlySpan<byte>)rawValue!);
        Assert.That(decoded!.Nonce, Is.EqualTo((UInt256)42));
        Assert.That(decoded.Balance, Is.EqualTo((UInt256)1000));
    }

    // ------- Key-mismatch tests -------

    [Test]
    public void TryRead_KeySharesPathButDivergesAtLeaf_ReturnsFalse()
    {
        // Add one account; try to read a different address whose hash diverges at the leaf.
        _stateTree.Set(TestItem.AddressA, new Account(1, 100));
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        // AddressB is not in the trie — its keccak may share part of AddressA's keccak path
        // but will diverge at the leaf.
        bool found = RlpTrieTraversal.TryRead(MakeStateLoader(), rootHash,
            KeccakCache.Compute(TestItem.AddressB.Bytes).Bytes, out byte[]? value);

        Assert.That(found, Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void TryRead_DiagnosticTrace_FailingAddress()
    {
        const int count = 300;
        Address[] addresses = new Address[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[20];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            addresses[i] = new Address(key);
        }

        for (int i = 0; i < addresses.Length; i++)
        {
            _stateTree.Set(addresses[i], new Account((ulong)(i + 1), (UInt256)(i + 1) * 100));
        }
        _stateTree.Commit();
        Hash256 rootHash = _stateTree.RootHash;

        // Find the failing address (0x21 = 33)
        Address failAddr = addresses[33];
        ReadOnlySpan<byte> addrPath = KeccakCache.Compute(failAddr.Bytes).Bytes;

        System.Text.StringBuilder trace = new();
        int loadCount = 0;
        RlpLoader tracingLoader = (TreePath path, Hash256 hash, ref TrieNodeRlp target) =>
        {
            byte[]? data = _trieStore.TryLoadRlp(path, hash);
            if (data is null) { trace.AppendLine($"  [{loadCount}] path={path} hash={hash} -> null"); return false; }
            if (data.Length > TrieNodeRlp.MaxRlpLength) { trace.AppendLine($"  [{loadCount}] path={path} -> too large ({data.Length})"); return false; }

            // Decode to see node type
            Rlp.ValueDecoderContext ctx = data.AsRlpValueContext();
            int seqLen = ctx.ReadSequenceLength();
            int items = ctx.PeekNumberOfItemsRemaining(ctx.Position + seqLen, maxSearch: 20);
            string nodeType = items == 2 ? "ext/leaf" : $"branch({items})";
            string hex = items == 2 ? Convert.ToHexString(data) : "";
            trace.AppendLine($"  [{loadCount++}] path={path} -> {data.Length}B {nodeType} {hex}");

            target.Set(data);
            return true;
        };

        trace.AppendLine($"Tracing TryRead for {failAddr}, keccak path = {KeccakCache.Compute(failAddr.Bytes)}");
        bool found = RlpTrieTraversal.TryRead(tracingLoader, rootHash, addrPath, out byte[]? rawValue);
        trace.AppendLine($"Result: found={found}");

        Account? fromStateTree = _stateTree.Get(failAddr, rootHash);
        trace.AppendLine($"StateTree.Get exists: {fromStateTree is not null}");

        Assert.That(found, Is.True, trace.ToString());
    }
}
