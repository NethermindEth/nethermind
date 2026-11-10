// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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

public class WitnessTrieValidationTests
{
    private const int KeyNibbles = 64;
    private static readonly byte[] StorageValue = Rlp.Encode(new byte[] { 0x01 }).Bytes;
    private static readonly byte[] AccountKey = Nibbles.BytesToNibbleBytes(Keccak.Compute(TestItem.AddressA.Bytes).Bytes);
    private static readonly StorageCell Cell = new(TestItem.AddressA, UInt256.One);

    /// <remarks>The node sits in a branch slot no lookup reaches, so it is never checked.</remarks>
    [TestCase(true, KeyNibbles + 1, TestName = "Leaf exceeding the key by a nibble")]
    [TestCase(true, 1, TestName = "Leaf short of the key")]
    [TestCase(false, 0, TestName = "Empty extension")]
    [TestCase(false, KeyNibbles, TestName = "Extension of the key's length")]
    public void Unreached_node_is_not_checked(bool isLeaf, int nibbles)
    {
        byte[] accountLeaf = AccountLeaf(0);
        byte[] unreachedNode = isLeaf ? Leaf(new byte[nibbles], StorageValue) : Extension(new byte[nibbles], AccountLeaf(0));
        byte[] root = Branch((AccountKey[0], accountLeaf), (AccountKey[0] ^ 1, unreachedNode));

        Assert.That(() => ReadBalance(root, accountLeaf, unreachedNode), Throws.Nothing);
    }

    public enum Malformation
    {
        None,
        InvalidRlp,
        BranchWithOneChild,
        BranchWithValue,
        BranchWithListValue,
        BranchWithShortHashReference,
        ExtensionToLeaf,
        ExtensionToNothing,
        OversizedEmbeddedChild,
        MalformedEmbeddedChild,
        HashReferenceToShortNode,
        NonCanonicalListHeader,
        NonCanonicalLeafValue,
        DeeplyNestedItem,
    }

    /// <remarks>The malformed node sits in the root's slot that the account's path leaves through, so the read reaches it.</remarks>
    [Test]
    public void Reached_node_must_be_well_formed([Values] Malformation malformation)
    {
        byte[] account = Rlp.Encode(new Account(UInt256.One)).Bytes;
        byte[] leaf = Leaf(AccountKey[2..], account);
        byte[] sibling = Leaf(new byte[KeyNibbles - 2], account);
        byte[] tinyLeaf = Leaf([1], StorageValue);
        byte[] smallLeaf = Leaf(AccountKey[1..], StorageValue);
        (Rlp slotReference, byte[][] nodes) = malformation switch
        {
            Malformation.None => (Reference(AccountLeaf(0)), [AccountLeaf(0)]),
            Malformation.InvalidRlp => Hashed(Enumerable.Repeat((byte)0xff, 40).ToArray()),
            Malformation.BranchWithOneChild => Hashed(Branch((AccountKey[1], leaf)), leaf),
            Malformation.BranchWithValue => Hashed(BranchOfReferences(Rlp.Encode(new byte[] { 1 }), (AccountKey[1], Reference(leaf)), (AccountKey[1] ^ 1, Reference(sibling))), leaf, sibling),
            Malformation.BranchWithListValue => Hashed(BranchOfReferences(new Rlp([0xc1, 0x80]), (AccountKey[1], Reference(leaf)), (AccountKey[1] ^ 1, Reference(sibling))), leaf, sibling),
            Malformation.BranchWithShortHashReference => Hashed(BranchOfReferences(Rlp.OfEmptyByteArray, (AccountKey[1], Reference(leaf)), (AccountKey[1] ^ 1, Rlp.Encode(new byte[Hash256.Size - 1]))), leaf),
            Malformation.ExtensionToLeaf => Hashed(Extension(AccountKey[1..2], leaf), leaf),
            Malformation.ExtensionToNothing => (new Rlp(Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(AccountKey[1..2], isLeaf: false)), Rlp.OfEmptyByteArray).Bytes), []),
            Malformation.OversizedEmbeddedChild => Hashed(BranchOfReferences(Rlp.OfEmptyByteArray, (AccountKey[1], new Rlp(leaf)), (AccountKey[1] ^ 1, Reference(sibling))), sibling),
            // In a slot the read does not take: the spec decodes the whole branch.
            Malformation.MalformedEmbeddedChild => Hashed(BranchOfReferences(Rlp.OfEmptyByteArray, (AccountKey[1], Reference(leaf)), (AccountKey[1] ^ 1, new Rlp([0xc1, 0x81]))), leaf),
            // A leaf short enough for a one-byte list header, given a long one.
            Malformation.NonCanonicalListHeader => Hashed([0xf8, (byte)(smallLeaf.Length - 1), .. smallLeaf.AsSpan(1)]),
            Malformation.NonCanonicalLeafValue => Hashed(Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(AccountKey[1..], isLeaf: true)), new Rlp([0x81, 0x01])).Bytes),
            // Deep enough to overflow the stack were it measured before its prefix is checked.
            Malformation.DeeplyNestedItem => Hashed(Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(AccountKey[1..2], isLeaf: false)), new Rlp(NestedLists(20_000))).Bytes),
            _ => Hashed(Branch((0, tinyLeaf), (1, tinyLeaf))),
        };
        byte[] root = BranchOfReferences(Rlp.OfEmptyByteArray, (AccountKey[0], slotReference), (AccountKey[0] ^ 1, Reference(sibling)));

        Assert.That(() => ReadBalance(root, [.. nodes, sibling]), malformation switch
        {
            Malformation.None => Throws.Nothing,
            Malformation.InvalidRlp => Throws.Exception,
            _ => Throws.TypeOf<TrieException>(),
        });
    }

    /// <remarks>The spec reads only the two low bits of a hex-prefix flag nibble, so 5 marks an extension of an odd path.</remarks>
    [Test]
    public void Extension_flag_takes_only_its_two_low_bits()
    {
        byte[] account = Rlp.Encode(new Account(UInt256.One)).Bytes;
        byte[] leaf = Leaf(AccountKey[3..], account);
        byte[] sibling = Leaf(new byte[KeyNibbles - 3], account);
        byte[] branch = Branch((AccountKey[2], leaf), (AccountKey[2] ^ 1, sibling));
        byte[] path = HexPrefix.ToBytes(AccountKey[1..2], isLeaf: false);
        path[0] |= 0x40;
        byte[] extension = Rlp.Encode(Rlp.Encode(path), Reference(branch)).Bytes;
        byte[] rootSibling = Leaf(new byte[KeyNibbles - 1], account);
        byte[] root = Branch((AccountKey[0], extension), (AccountKey[0] ^ 1, rootSibling));

        Assert.That(() => ReadBalance(root, extension, branch, leaf, sibling, rootSibling), Throws.Nothing);
    }

    /// <remarks>The merged extension takes the sibling's reference to its child, so the child, absent from the witness, is
    /// never reached.</remarks>
    [Test]
    public void Delete_collapsing_into_an_extension_does_not_reach_its_child()
    {
        byte[] slotKey = SlotKey();
        byte[] slotLeaf = Leaf(slotKey[1..], StorageValue);
        byte[] sibling = Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes([0], isLeaf: false)), Rlp.Encode(Keccak.Compute("branch"u8).Bytes)).Bytes;
        byte[] storageRoot = Branch((slotKey[0], slotLeaf), (slotKey[0] ^ 1, sibling));

        Assert.That(() => AccessStorage(storageRoot, [slotLeaf, sibling], StorageAccess.Write, UInt256.Zero), Throws.Nothing);
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

    public enum InlinedNode { SlotLeaf, Sibling }

    public enum SlotAccess { Read, Write, Delete }

    /// <remarks>
    /// Below an extension of 60 nibbles, a branch and its leaves are short enough to be inlined in their parents, so they are
    /// resolved from that RLP rather than loaded. An overlong slot leaf is visited by every access; an overlong sibling only
    /// by a delete that collapses the branch into it.
    /// </remarks>
    [Test]
    public void Inlined_leaf_is_checked_once_visited([Values] InlinedNode overlong, [Values] SlotAccess access)
    {
        const int ExtensionNibbles = 60;
        byte[] slotKey = SlotKey();
        byte[] slotLeafPath = slotKey[(ExtensionNibbles + 1)..];
        byte[] slotLeaf = Leaf(overlong == InlinedNode.SlotLeaf ? [0, .. slotLeafPath] : slotLeafPath, StorageValue);
        byte[] sibling = Leaf(new byte[KeyNibbles - ExtensionNibbles - (overlong == InlinedNode.Sibling ? 0 : 1)], StorageValue);
        byte[] branch = Branch((slotKey[ExtensionNibbles], slotLeaf), (slotKey[ExtensionNibbles] ^ 1, sibling));
        byte[] extension = Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(slotKey[..ExtensionNibbles], isLeaf: false)), Reference(branch)).Bytes;
        Assert.That(branch, Has.Length.LessThan(Hash256.Size), "the branch and its leaves must be inlined");

        bool visited = overlong == InlinedNode.SlotLeaf || access == SlotAccess.Delete;
        Assert.That(() => AccessStorage(extension, [], access == SlotAccess.Read ? StorageAccess.Read : StorageAccess.Write, access == SlotAccess.Delete ? UInt256.Zero : 2),
            visited ? Throws.TypeOf<TrieException>() : Throws.Nothing);
    }

    /// <remarks>
    /// An empty extension over an inlined branch is short enough to be inlined too, so only the walk that visits it can reject
    /// it. On the slot's path every access visits it; as the slot leaf's sibling only a delete that collapses the branch into it.
    /// </remarks>
    [Test]
    public void Inlined_empty_extension_is_rejected_once_visited([Values] bool onSlotPath, [Values] SlotAccess access)
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
            onSlotPath || access == SlotAccess.Delete ? Throws.TypeOf<TrieException>() : Throws.Nothing);
    }

    /// <summary>An RLP list nested <paramref name="depth"/> deep, each holding the next, around an empty one.</summary>
    private static byte[] NestedLists(int depth)
    {
        int[] contentLengths = new int[depth + 1];
        for (int i = 1; i <= depth; i++) contentLengths[i] = contentLengths[i - 1] + HeaderLength(contentLengths[i - 1]);

        byte[] lists = new byte[contentLengths[depth] + HeaderLength(contentLengths[depth])];
        int position = 0;
        for (int i = depth; i >= 0; i--)
        {
            int length = contentLengths[i];
            byte[] header = length < 56 ? [(byte)(0xc0 + length)] : length < 256 ? [0xf8, (byte)length] : [0xf9, (byte)(length >> 8), (byte)length];
            header.CopyTo(lists, position);
            position += header.Length;
        }

        return lists;

        static int HeaderLength(int length) => length < 56 ? 1 : length < 256 ? 2 : 3;
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

    private static byte[] Branch(params (int Slot, byte[] Child)[] children) =>
        BranchOfReferences(Rlp.OfEmptyByteArray, Array.ConvertAll(children, c => (c.Slot, Reference(c.Child))));

    private static byte[] BranchOfReferences(Rlp value, params (int Slot, Rlp Reference)[] children)
    {
        Rlp[] items = new Rlp[17];
        Array.Fill(items, Rlp.OfEmptyByteArray);
        foreach ((int slot, Rlp reference) in children)
        {
            items[slot] = reference;
        }

        items[16] = value;
        return Rlp.Encode(items).Bytes;
    }

    /// <summary>A reference to <paramref name="node"/> by its hash whatever its length, with the witness nodes it needs.</summary>
    private static (Rlp Reference, byte[][] Nodes) Hashed(byte[] node, params byte[][] children) =>
        (Rlp.Encode(Keccak.Compute(node).Bytes), [node, .. children]);

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
