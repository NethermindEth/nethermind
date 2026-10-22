// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class SecuredTriePathTests
{
    private const int KeyNibbles = 64;
    private static readonly byte[] StorageValue = Rlp.Encode(new byte[] { 0x01 }).Bytes;
    private static readonly byte[] AccountKey = Nibbles.BytesToNibbleBytes(Keccak.Compute(TestItem.AddressA.Bytes).Bytes);
    private static readonly StorageCell Cell = new(TestItem.AddressA, UInt256.One);

    /// <remarks>The node sits in a branch slot no lookup reaches, so only its own path is checked, never its depth.</remarks>
    [TestCase(true, KeyNibbles - 1, true, TestName = "Leaf ending at the key")]
    [TestCase(true, KeyNibbles, true, TestName = "Leaf exceeding the key across the branch")]
    [TestCase(true, KeyNibbles + 1, false, TestName = "Leaf exceeding the key by a nibble")]
    [TestCase(true, KeyNibbles + 3, false, TestName = "Leaf exceeding the key by an odd count")]
    [TestCase(false, 1, true, TestName = "Extension of a nibble")]
    [TestCase(false, KeyNibbles - 1, true, TestName = "Extension short of the key")]
    [TestCase(false, 0, false, TestName = "Empty extension")]
    [TestCase(false, KeyNibbles, false, TestName = "Extension of the key's length")]
    public void Witness_node_is_checked_against_its_own_path_only(bool isLeaf, int nibbles, bool valid)
    {
        byte[] accountLeaf = AccountLeaf(0);
        byte[] unreadNode = isLeaf
            ? Leaf(new byte[nibbles], StorageValue)
            : Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(new byte[nibbles], isLeaf: false)), Rlp.Encode(Keccak.Compute([0x01]).Bytes)).Bytes;
        byte[] root = Branch((AccountKey[0], accountLeaf), (AccountKey[0] ^ 1, unreadNode));

        Assert.That(() => ReadBalance(root, accountLeaf, unreadNode), valid ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    [Test]
    public void Read_leaf_must_end_at_the_key([Values(-1, 0, 1)] int extraNibbles)
    {
        byte[] accountLeaf = AccountLeaf(extraNibbles);
        byte[] root = Branch((AccountKey[0], accountLeaf), (AccountKey[0] ^ 1, AccountLeaf(0)));

        Assert.That(() => ReadBalance(root, accountLeaf), extraNibbles == 0 ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    public enum StorageAccess { Read, Write, Clear, ClearThenWrite }

    /// <remarks>The slot's leaf is checked once a read or a write visits it; a clear discards the old storage unvisited.</remarks>
    [Test]
    public void Storage_leaf_is_checked_once_visited([Values] StorageAccess access, [Values] bool overlong)
    {
        byte[] slotKey = SlotKey();
        byte[] slotLeaf = Leaf(overlong ? [0, .. slotKey[1..]] : slotKey[1..], StorageValue);
        byte[] storageRoot = Branch((slotKey[0], slotLeaf), (slotKey[0] ^ 1, Leaf(new byte[KeyNibbles - 1], StorageValue)));

        Assert.That(() => AccessStorage(storageRoot, [slotLeaf], access, UInt256.One),
            overlong && access is StorageAccess.Read or StorageAccess.Write ? Throws.TypeOf<TrieException>() : Throws.Nothing);
    }

    /// <remarks>Deleting the slot collapses the branch into the sibling, which only then is visited.</remarks>
    [Test]
    public void Sibling_is_checked_once_a_delete_collapses_into_it([Values] bool overlong, [Values] bool delete)
    {
        byte[] slotKey = SlotKey();
        byte[] sibling = Leaf(new byte[KeyNibbles - (overlong ? 0 : 1)], StorageValue);
        byte[] slotLeaf = Leaf(slotKey[1..], StorageValue);
        byte[] storageRoot = Branch((slotKey[0], slotLeaf), (slotKey[0] ^ 1, sibling));

        Assert.That(() => AccessStorage(storageRoot, [slotLeaf, sibling], StorageAccess.Write, delete ? UInt256.Zero : 2),
            overlong && delete ? Throws.TypeOf<TrieException>() : Throws.Nothing);
    }

    private static void ReadBalance(byte[] root, params byte[][] nodes)
    {
        using Witness witness = CreateWitness([.. nodes, root]);
        StatelessBlockProcessingEnv env = new(witness, new TestSpecProvider(Amsterdam.Instance), Always.Valid, NullLogManager.Instance);
        using IDisposable scope = env.WorldState.BeginScope(Build.A.BlockHeader.WithStateRoot(Keccak.Compute(root)).TestObject);

        Assert.That(env.WorldState.GetBalance(TestItem.AddressA), Is.EqualTo(UInt256.One));
    }

    private static void AccessStorage(byte[] storageRoot, byte[][] storageNodes, StorageAccess access, UInt256 value)
    {
        Account account = new Account(UInt256.One).WithChangedStorageRoot(Keccak.Compute(storageRoot));
        byte[] stateRoot = Leaf(AccountKey, Rlp.Encode(account).Bytes);
        using Witness witness = CreateWitness([stateRoot, storageRoot, .. storageNodes]);
        StatelessBlockProcessingEnv env = new(witness, new TestSpecProvider(Amsterdam.Instance), Always.Valid, NullLogManager.Instance);
        using IDisposable scope = env.WorldState.BeginScope(Build.A.BlockHeader.WithStateRoot(Keccak.Compute(stateRoot)).TestObject);

        switch (access)
        {
            case StorageAccess.Read:
                env.WorldState.Get(Cell, out _);
                return;
            case StorageAccess.Write:
                env.WorldState.Set(Cell, value);
                break;
            case StorageAccess.Clear:
                env.WorldState.ClearStorage(TestItem.AddressA);
                break;
            case StorageAccess.ClearThenWrite:
                env.WorldState.ClearStorage(TestItem.AddressA);
                env.WorldState.Set(Cell, value);
                break;
        }

        env.WorldState.Commit(Amsterdam.Instance);
        env.WorldState.CommitTree(1);
    }

    private static byte[] SlotKey()
    {
        ValueHash256 key = default;
        StorageTree.ComputeKeyWithLookup(Cell.Index, ref key);
        return Nibbles.BytesToNibbleBytes(key.Bytes);
    }

    /// <summary>The account leaf of <see cref="TestItem.AddressA"/> below the root branch, its path lengthened by
    /// <paramref name="extraNibbles"/>.</summary>
    private static byte[] AccountLeaf(int extraNibbles)
    {
        byte[] path = new byte[KeyNibbles - 1 + extraNibbles];
        AccountKey.AsSpan(1, Math.Min(path.Length, KeyNibbles - 1)).CopyTo(path);
        return Leaf(path, Rlp.Encode(new Account(UInt256.One)).Bytes);
    }

    private static byte[] Leaf(byte[] nibbles, byte[] value) =>
        Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(nibbles, isLeaf: true)), Rlp.Encode(value)).Bytes;

    private static byte[] Branch(params (int Slot, byte[] Child)[] children)
    {
        Rlp[] items = new Rlp[17];
        Array.Fill(items, Rlp.OfEmptyByteArray);
        foreach ((int slot, byte[] child) in children)
        {
            items[slot] = child.Length < Hash256.Size ? new Rlp(child) : Rlp.Encode(Keccak.Compute(child).Bytes);
        }

        return Rlp.Encode(items).Bytes;
    }

    private static Witness CreateWitness(byte[][] state) => new()
    {
        Codes = ArrayPoolList<byte[]>.Empty(),
        State = new ArrayPoolList<byte[]>(state),
        Keys = ArrayPoolList<byte[]>.Empty(),
        Headers = ArrayPoolList<byte[]>.Empty()
    };
}
