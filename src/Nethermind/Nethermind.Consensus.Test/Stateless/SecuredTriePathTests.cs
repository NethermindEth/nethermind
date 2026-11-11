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
    private const int BranchChildren = 16;
    private static readonly byte[] StorageValue = Rlp.Encode(new byte[] { 0x01 }).Bytes;
    private static readonly byte[] AccountKey = Nibbles.BytesToNibbleBytes(Keccak.Compute(TestItem.AddressA.Bytes).Bytes);
    private static readonly StorageCell Cell = new(TestItem.AddressA, UInt256.One);

    /// <remarks>The node sits in a branch slot no lookup reaches, so only the walk of the state trie checks it against its depth.</remarks>
    [TestCase(true, KeyNibbles - 1, true, TestName = "Leaf ending at the key")]
    [TestCase(true, KeyNibbles - 2, false, TestName = "Leaf ending before the key")]
    [TestCase(true, KeyNibbles, false, TestName = "Leaf exceeding the key across the branch")]
    [TestCase(true, KeyNibbles + 1, false, TestName = "Leaf exceeding the key by a nibble")]
    [TestCase(true, KeyNibbles + 3, false, TestName = "Leaf exceeding the key by an odd count")]
    [TestCase(false, 1, true, TestName = "Extension of a nibble")]
    [TestCase(false, KeyNibbles - 2, true, TestName = "Extension short of the key")]
    [TestCase(false, KeyNibbles - 1, false, TestName = "Extension reaching the key across the branch")]
    [TestCase(false, 0, false, TestName = "Empty extension")]
    [TestCase(false, KeyNibbles, false, TestName = "Extension of the key's length")]
    public void Unread_witness_node_is_checked_against_its_depth(bool isLeaf, int nibbles, bool valid)
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

    /// <remarks>The spec checks the paths of the nodes a decoded trie reaches, and only hashes a witness node no trie reaches.</remarks>
    [Test]
    public void Witness_node_no_trie_reaches_is_not_checked()
    {
        byte[] accountLeaf = AccountLeaf(0);
        byte[] root = Branch((AccountKey[0], accountLeaf), (AccountKey[0] ^ 1, AccountLeaf(0)));
        byte[] disconnectedLeaf = Leaf(new byte[KeyNibbles + 1], [0x01]);
        Assert.That(disconnectedLeaf, Has.Length.EqualTo(36));

        Assert.That(() => ReadBalance(root, accountLeaf, disconnectedLeaf), Throws.Nothing);
    }

    public enum StorageAccess { Read, Write, Clear, ClearThenWrite }

    /// <remarks>The slot's leaf is checked once a read or a write decodes the storage trie; a clear discards the old storage
    /// undecoded.</remarks>
    [Test]
    public void Storage_leaf_is_checked_once_visited([Values] StorageAccess access, [Values] bool overlong)
    {
        byte[] slotKey = SlotKey();
        byte[] slotLeaf = Leaf(overlong ? [0, .. slotKey[1..]] : slotKey[1..], StorageValue);
        byte[] storageRoot = Branch((slotKey[0], slotLeaf), (slotKey[0] ^ 1, Leaf(new byte[KeyNibbles - 1], StorageValue)));

        Assert.That(() => AccessStorage(storageRoot, [slotLeaf], access, UInt256.One),
            overlong && access is StorageAccess.Read or StorageAccess.Write ? Throws.TypeOf<TrieException>() : Throws.Nothing);
    }

    /// <remarks>A write decodes the storage trie, whose walk reaches the sibling whether or not a delete collapses the branch
    /// into it.</remarks>
    [Test]
    public void Sibling_is_checked_once_its_storage_trie_is_decoded([Values] bool overlong, [Values] bool delete)
    {
        byte[] slotKey = SlotKey();
        byte[] sibling = Leaf(new byte[KeyNibbles - (overlong ? 0 : 1)], StorageValue);
        byte[] slotLeaf = Leaf(slotKey[1..], StorageValue);
        byte[] storageRoot = Branch((slotKey[0], slotLeaf), (slotKey[0] ^ 1, sibling));

        Assert.That(() => AccessStorage(storageRoot, [slotLeaf, sibling], StorageAccess.Write, delete ? UInt256.Zero : 2),
            overlong ? Throws.TypeOf<TrieException>() : Throws.Nothing);
    }

    public enum InlinedNode { SlotLeaf, Sibling }

    public enum SlotAccess { Read, Write, Delete }

    /// <remarks>
    /// Below an extension of 60 nibbles, a branch and its leaves are short enough to be inlined in their parents, so they are
    /// resolved from that RLP rather than loaded. Every access decodes the storage trie, whose walk reaches both leaves.
    /// </remarks>
    [Test]
    public void Inlined_leaf_is_checked_once_its_storage_trie_is_decoded([Values] InlinedNode overlong, [Values] SlotAccess access)
    {
        const int ExtensionNibbles = 60;
        byte[] slotKey = SlotKey();
        byte[] slotLeafPath = slotKey[(ExtensionNibbles + 1)..];
        byte[] slotLeaf = Leaf(overlong == InlinedNode.SlotLeaf ? [0, .. slotLeafPath] : slotLeafPath, StorageValue);
        byte[] sibling = Leaf(new byte[KeyNibbles - ExtensionNibbles - (overlong == InlinedNode.Sibling ? 0 : 1)], StorageValue);
        byte[] branch = Branch((slotKey[ExtensionNibbles], slotLeaf), (slotKey[ExtensionNibbles] ^ 1, sibling));
        byte[] extension = Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(slotKey[..ExtensionNibbles], isLeaf: false)), Reference(branch)).Bytes;
        Assert.That(branch, Has.Length.LessThan(Hash256.Size), "the branch and its leaves must be inlined");

        Assert.That(() => AccessStorage(extension, [], access == SlotAccess.Read ? StorageAccess.Read : StorageAccess.Write, access == SlotAccess.Delete ? UInt256.Zero : 2),
            Throws.TypeOf<TrieException>());
    }

    /// <remarks>
    /// An empty extension over an inlined branch is short enough to be inlined too, so only a walk that reaches it can reject
    /// it. On the slot's path or off it, every access decodes the storage trie, whose walk reaches it.
    /// </remarks>
    [Test]
    public void Inlined_empty_extension_is_rejected_once_its_storage_trie_is_decoded([Values] bool onSlotPath, [Values] SlotAccess access)
    {
        const int ExtensionNibbles = 60;
        byte[] slotKey = SlotKey();
        byte[] emptyExtension;
        byte[] extensionChild;
        if (onSlotPath)
        {
            emptyExtension = Extension([], Branch(
                (slotKey[ExtensionNibbles], Leaf(slotKey[(ExtensionNibbles + 1)..], StorageValue)),
                (slotKey[ExtensionNibbles] ^ 1, Leaf(new byte[KeyNibbles - ExtensionNibbles - 1], StorageValue))));
            extensionChild = emptyExtension;
        }
        else
        {
            byte[] siblingLeaf = Leaf(new byte[KeyNibbles - ExtensionNibbles - 2], StorageValue);
            emptyExtension = Extension([], Branch((0, siblingLeaf), (1, siblingLeaf)));
            extensionChild = Branch((slotKey[ExtensionNibbles], Leaf(slotKey[(ExtensionNibbles + 1)..], StorageValue)), (slotKey[ExtensionNibbles] ^ 1, emptyExtension));
        }

        Assert.That(emptyExtension, Has.Length.LessThan(Hash256.Size), "the empty extension must be inlined");

        byte[] storageRoot = Extension(slotKey[..ExtensionNibbles], extensionChild);
        Assert.That(() => AccessStorage(storageRoot, onSlotPath ? [] : [extensionChild], access == SlotAccess.Read ? StorageAccess.Read : StorageAccess.Write, access == SlotAccess.Delete ? UInt256.Zero : 2),
            Throws.TypeOf<TrieException>());
    }

    /// <remarks>The path of each node a lookup reaches ends at the key, but the branch's other child, a leaf as long as a key,
    /// would end a nibble past it, as in the zkEVM fixture <c>cumulative_path_exceeds_key_length</c>.</remarks>
    [Test]
    public void Scope_opens_only_when_every_state_trie_path_ends_at_the_key([Values] bool valid)
    {
        byte[] accountLeaf = AccountLeaf(0);
        byte[] unreadLeaf = Leaf(new byte[KeyNibbles - (valid ? 1 : 0)], StorageValue);
        byte[] root = Branch((AccountKey[0], accountLeaf), (AccountKey[0] ^ 1, unreadLeaf));

        Assert.That(() => OpenScope(root, accountLeaf, unreadLeaf), valid ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    /// <remarks>The trie decoder reads a node's items up to the end of its bytes, past a list that ends before them or claims
    /// more, so a child placed there would be decoded without the walk reaching it.</remarks>
    [Test]
    public void Scope_opens_only_when_every_node_s_list_fills_its_bytes([Values(-1, 0, 1)] int extraBytes)
    {
        byte[] accountLeaf = AccountLeaf(0);
        byte[] branch = Branch((AccountKey[0], accountLeaf), (AccountKey[0] ^ 1, AccountLeaf(0)));
        byte[] root = extraBytes < 0 ? branch[..^1] : [.. branch, .. new byte[extraBytes]];

        Assert.That(() => OpenScope(root, accountLeaf), extraBytes == 0 ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    /// <remarks>Every path to the leaf is checked, yet each node once per depth once the walk has read twice the witness'
    /// bytes: walked path by path, this tree of 16^16 leaves would not finish. The leaf is short enough to be inlined in the
    /// branch above it.</remarks>
    [Test]
    public void Scope_checks_a_node_every_child_shares_once_per_depth([Values] bool valid)
    {
        const int Levels = 16;
        byte[] node = Leaf(new byte[KeyNibbles - Levels + (valid ? 0 : 1)], [0x01]);
        byte[][] state = new byte[Levels + 1][];
        state[0] = node;
        for (int level = 1; level <= Levels; level++)
        {
            (int, byte[])[] children = new (int, byte[])[BranchChildren];
            for (int i = 0; i < BranchChildren; i++) children[i] = (i, node);
            state[level] = node = Branch(children);
        }

        Assert.That(() => OpenScope(node, state), valid ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    private static void OpenScope(byte[] root, params byte[][] nodes)
    {
        using Witness witness = CreateWitness([.. nodes, root]);
        StatelessBlockProcessingEnv env = new(witness, new TestSpecProvider(Amsterdam.Instance), Always.Valid, NullLogManager.Instance);
        using IDisposable scope = env.WorldState.BeginScope(Build.A.BlockHeader.WithStateRoot(Keccak.Compute(root)).TestObject);
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

    private static byte[] Extension(byte[] nibbles, byte[] child) =>
        Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(nibbles, isLeaf: false)), Reference(child)).Bytes;

    private static byte[] Branch(params (int Slot, byte[] Child)[] children)
    {
        Rlp[] items = new Rlp[17];
        Array.Fill(items, Rlp.OfEmptyByteArray);
        foreach ((int slot, byte[] child) in children)
        {
            items[slot] = Reference(child);
        }

        return Rlp.Encode(items).Bytes;
    }

    /// <summary>A parent's reference to <paramref name="child"/>: the child itself when shorter than a keccak, as a trie inlines it.</summary>
    private static Rlp Reference(byte[] child) => child.Length < Hash256.Size ? new Rlp(child) : Rlp.Encode(Keccak.Compute(child).Bytes);

    private static Witness CreateWitness(byte[][] state) => new()
    {
        Codes = ArrayPoolList<byte[]>.Empty(),
        State = new ArrayPoolList<byte[]>(state),
        Keys = ArrayPoolList<byte[]>.Empty(),
        Headers = ArrayPoolList<byte[]>.Empty()
    };
}
