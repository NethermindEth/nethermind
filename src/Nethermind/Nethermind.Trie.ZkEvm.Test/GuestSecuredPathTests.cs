// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Tests for the guest's checks of the secured-trie witness nodes its raw-key walks reach.</summary>
/// <remarks>
/// Each node is keyed by a made-up hash rather than its keccak: in a ZK_EVM build the Keccak permutation is a zkVM precompile
/// that throws outside the guest. The walks never hash a node they read, so they cannot tell.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class GuestSecuredPathTests
{
    private const int KeyNibbles = 64;
    private static readonly byte[] Key = RawKey(1);
    private static readonly byte[] KeyPath = Nibbles.BytesToNibbleBytes(Key);

    [Test]
    public void Reached_leaf_must_end_at_the_key([Values(-1, 0, 1)] int extraNibbles, [Values] bool write)
    {
        Witness witness = new();
        PatriciaTree tree = witness.Tree(witness.Branch((KeyPath[0], witness.Leaf(LeafPath(extraNibbles))), (KeyPath[0] ^ 1, witness.Leaf(new byte[KeyNibbles - 1]))));

        Assert.That(() => ReadOrWrite(tree, write), extraNibbles == 0 ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    /// <remarks>An extension reaching the key's length, or a branch below it, leaves no nibble for the leaf. An empty
    /// extension is invalid at any depth.</remarks>
    [Test]
    public void Reached_extension_and_branch_must_stay_short_of_the_key([Values(0, KeyNibbles - 2, KeyNibbles - 1, KeyNibbles)] int extensionNibbles, [Values] bool write)
    {
        Witness witness = new();
        int branchDepth = Math.Min(extensionNibbles, KeyNibbles - 1);
        byte[] leafPath = KeyPath[Math.Min(extensionNibbles + 1, KeyNibbles)..];
        byte[] branch = witness.Branch((KeyPath[branchDepth], witness.Leaf(leafPath)), (KeyPath[branchDepth] ^ 1, witness.Leaf(new byte[leafPath.Length])));
        PatriciaTree tree = witness.Tree(witness.Extension(KeyPath[..extensionNibbles], branch));

        Assert.That(() => ReadOrWrite(tree, write), extensionNibbles is > 0 and < KeyNibbles ? Throws.Nothing : Throws.TypeOf<TrieException>());
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
        HighFlagExtensionToLeaf,
        ExtensionToNothing,
        EmptyListChild,
        OversizedEmbeddedChild,
        MalformedEmbeddedChild,
        HashReferenceToShortNode,
        NonCanonicalListHeader,
        NonCanonicalLeafValue,
        DeeplyNestedItem,
    }

    /// <remarks>The malformed node sits in the root's slot that the key leads through, so every read and write reaches it.</remarks>
    [Test]
    public void Reached_node_must_be_well_formed([Values] Malformation malformation, [Values] bool write)
    {
        Witness witness = new();
        byte[] slotChild = malformation switch
        {
            Malformation.None => witness.Leaf(LeafPath(0)),
            Malformation.InvalidRlp => witness.Hashed(Enumerable.Repeat((byte)0xff, 40).ToArray()),
            Malformation.BranchWithOneChild => witness.Branch((KeyPath[1], witness.Leaf(KeyPath[2..]))),
            Malformation.BranchWithValue => witness.Branch([1], (KeyPath[1], witness.Leaf(KeyPath[2..])), (KeyPath[1] ^ 1, witness.Leaf(new byte[KeyNibbles - 2]))),
            Malformation.BranchWithListValue => Witness.BranchOfReferences(new Rlp([0xc1, 0x80]),
                (KeyPath[1], witness.Reference(witness.Leaf(KeyPath[2..]))), (KeyPath[1] ^ 1, witness.Reference(witness.Leaf(new byte[KeyNibbles - 2])))),
            Malformation.BranchWithShortHashReference => Witness.BranchOfReferences(Rlp.OfEmptyByteArray,
                (KeyPath[1], witness.Reference(witness.Leaf(KeyPath[2..]))), (KeyPath[1] ^ 1, Rlp.Encode(new byte[Hash256.Size - 1]))),
            Malformation.ExtensionToLeaf => witness.Extension(KeyPath[1..2], witness.Leaf(KeyPath[2..])),
            // Flag nibble 5: an extension, as the spec reads only its two low bits.
            Malformation.HighFlagExtensionToLeaf => HighFlagExtension(witness, KeyPath[1..2], witness.Leaf(KeyPath[2..])),
            Malformation.ExtensionToNothing => witness.ExtensionOfReference(KeyPath[1..2], Rlp.OfEmptyByteArray),
            // Accepted in its parent, as an embedded node, but a node of no items once reached.
            Malformation.EmptyListChild => [0xc0],
            Malformation.OversizedEmbeddedChild => Witness.BranchOfReferences(Rlp.OfEmptyByteArray,
                (KeyPath[1], new Rlp(witness.Leaf(KeyPath[2..]))), (KeyPath[1] ^ 1, witness.Reference(witness.Leaf(new byte[KeyNibbles - 2])))),
            // In a slot the walk does not take: the spec decodes the whole branch.
            Malformation.MalformedEmbeddedChild => Witness.BranchOfReferences(Rlp.OfEmptyByteArray,
                (KeyPath[1], witness.Reference(witness.Leaf(KeyPath[2..]))), (KeyPath[1] ^ 1, new Rlp([0xc1, 0x81]))),
            Malformation.NonCanonicalListHeader => LongListHeader(witness.Leaf(LeafPath(0))),
            Malformation.NonCanonicalLeafValue => Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(LeafPath(0), isLeaf: true)), new Rlp([0x81, 0x01])).Bytes,
            // Deep enough to overflow the stack were it measured before its prefix is checked.
            Malformation.DeeplyNestedItem => Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(KeyPath[1..2], isLeaf: false)), new Rlp(NestedLists(20_000))).Bytes,
            _ => witness.Hashed(witness.Branch((0, witness.Leaf([1])), (1, witness.Leaf([1])))),
        };
        PatriciaTree tree = witness.Tree(witness.Branch((KeyPath[0], slotChild), (KeyPath[0] ^ 1, witness.Leaf(new byte[KeyNibbles - 1]))));

        Assert.That(() => ReadOrWrite(tree, write), malformation == Malformation.None ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    /// <remarks>A key ends 64 nibbles below the root, so no branch can sit there, whether all its children are hashed or only
    /// some.</remarks>
    [Test]
    public void Branch_at_the_key_length_is_rejected([Values] bool full, [Values] bool write)
    {
        Witness witness = new();
        (int Slot, Rlp Reference)[] children = new (int, Rlp)[full ? 16 : 2];
        for (int i = 0; i < children.Length; i++) children[i] = (i, Rlp.Encode(FakeHash(0x100 + i).Bytes));
        byte[] branchAtKeyLength = Witness.BranchOfReferences(Rlp.OfEmptyByteArray, children);
        byte[] branch = witness.Branch((KeyPath[KeyNibbles - 1], branchAtKeyLength), (KeyPath[KeyNibbles - 1] ^ 1, witness.Leaf([])));
        PatriciaTree tree = witness.Tree(witness.Extension(KeyPath[..(KeyNibbles - 1)], branch));

        Assert.That(() => ReadOrWrite(tree, write), Throws.TypeOf<TrieException>());
    }

    /// <remarks>The spec reads only the two low bits of a hex-prefix flag nibble, so 5 marks an extension of an odd path.</remarks>
    [Test]
    public void Extension_flag_takes_only_its_two_low_bits()
    {
        Witness witness = new();
        byte[] branch = witness.Branch((KeyPath[2], witness.Leaf(KeyPath[3..])), (KeyPath[2] ^ 1, witness.Leaf(new byte[KeyNibbles - 3])));
        byte[] extension = HighFlagExtension(witness, KeyPath[1..2], branch);
        PatriciaTree tree = witness.Tree(witness.Branch((KeyPath[0], extension), (KeyPath[0] ^ 1, witness.Leaf(new byte[KeyNibbles - 1]))));

        Assert.That(tree.Get(Key).ToArray(), Is.EqualTo(new byte[] { 1 }));
    }

    /// <remarks>An empty list in a slot no walk takes is accepted too: the spec takes it for an embedded node.</remarks>
    [Test]
    public void Unreached_node_is_not_checked([Values] bool write)
    {
        Witness witness = new();
        byte[] unreached = witness.Branch([1], (0, witness.Leaf(new byte[KeyNibbles])), (1, witness.Leaf(new byte[KeyNibbles])));
        PatriciaTree tree = witness.Tree(Witness.BranchOfReferences(Rlp.OfEmptyByteArray,
            (KeyPath[0], witness.Reference(witness.Leaf(LeafPath(0)))), (KeyPath[0] ^ 1, witness.Reference(unreached)), (KeyPath[0] ^ 2, new Rlp([0xc0]))));

        Assert.That(() => ReadOrWrite(tree, write), Throws.Nothing);
    }

    /// <remarks>Deleting the key collapses the branch into its sibling, which only then is reached.</remarks>
    [Test]
    public void Sibling_is_checked_once_a_delete_collapses_into_it([Values(-1, 0, 1)] int extraNibbles)
    {
        Witness witness = new();
        PatriciaTree tree = witness.Tree(witness.Branch((KeyPath[0], witness.Leaf(LeafPath(0))), (KeyPath[0] ^ 1, witness.Leaf(new byte[KeyNibbles - 1 + extraNibbles]))));

        Assert.That(() => tree.Set(Key, Array.Empty<byte>()), extraNibbles == 0 ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    /// <remarks>An empty extension sibling would merge with the branch's nibble into a valid extension of one nibble.</remarks>
    [Test]
    public void Empty_extension_sibling_is_checked_once_a_delete_collapses_into_it([Values(0, 1)] int siblingNibbles)
    {
        Witness witness = new();
        byte[] leafPath = new byte[KeyNibbles - 2 - siblingNibbles];
        byte[] sibling = witness.Extension(new byte[siblingNibbles], witness.Branch((0, witness.Leaf(leafPath)), (1, witness.Leaf(leafPath))));
        PatriciaTree tree = witness.Tree(witness.Branch((KeyPath[0], witness.Leaf(LeafPath(0))), (KeyPath[0] ^ 1, sibling)));

        Assert.That(() => tree.Set(Key, Array.Empty<byte>()), siblingNibbles == 0 ? Throws.TypeOf<TrieException>() : Throws.Nothing);
    }

    /// <remarks>The sibling's depth counts the parent extension's nibbles; one more of them pushes each kind of sibling past the key.</remarks>
    [Test]
    public void Sibling_below_an_extension_is_checked_once_a_delete_collapses_into_it(
        [Values(NodeType.Leaf, NodeType.Extension, NodeType.Branch)] NodeType siblingType, [Values] bool overlength)
    {
        Witness witness = new();
        int extensionNibbles = (siblingType == NodeType.Extension ? KeyNibbles - 3 : KeyNibbles - 2) + (overlength ? 1 : 0);
        int index = KeyPath[extensionNibbles];
        byte[] sibling = siblingType switch
        {
            NodeType.Leaf => witness.Leaf([0]),
            NodeType.Extension => witness.Extension([0], witness.Branch((0, witness.Leaf([])), (1, witness.Leaf([])))),
            _ => witness.Branch((0, witness.Leaf([])), (1, witness.Leaf([]))),
        };
        byte[] branch = witness.Branch((index, witness.Leaf(KeyPath[(extensionNibbles + 1)..])), (index ^ 1, sibling));
        PatriciaTree tree = witness.Tree(witness.Extension(KeyPath[..extensionNibbles], branch));

        Assert.That(() => tree.Set(Key, Array.Empty<byte>()), overlength ? Throws.TypeOf<TrieException>() : Throws.Nothing);
    }

    /// <remarks>The merged extension takes the sibling's reference to its child, so the child, absent from the witness, is
    /// never reached.</remarks>
    [Test]
    public void Delete_collapsing_into_an_extension_does_not_reach_its_child()
    {
        Witness witness = new();
        byte[] sibling = witness.ExtensionOfReference([0], Rlp.Encode(FakeHash(0xff).Bytes));
        PatriciaTree tree = witness.Tree(witness.Branch((KeyPath[0], witness.Leaf(LeafPath(0))), (KeyPath[0] ^ 1, sibling)));

        Assert.That(() => tree.Set(Key, Array.Empty<byte>()), Throws.Nothing);
    }

    [Test]
    public void Unsecured_trie_lookup_misses_a_leaf_of_another_length()
    {
        byte[] shortKey = [0x12, 0x34];
        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch[1] = DecodedLeaf([2, 3, 4, 5]);
        PatriciaTree tree = new() { RootRef = branch };

        Assert.That(tree.Get(shortKey).IsEmpty, Is.True);
    }

    private static void ReadOrWrite(PatriciaTree tree, bool write)
    {
        if (write) tree.Set(Key, [2]);
        else tree.Get(Key);
    }

    /// <summary>The key's path below a root branch, lengthened by <paramref name="extraNibbles"/>.</summary>
    private static byte[] LeafPath(int extraNibbles)
    {
        byte[] path = new byte[KeyNibbles - 1 + extraNibbles];
        KeyPath.AsSpan(1, Math.Min(path.Length, KeyNibbles - 1)).CopyTo(path);
        return path;
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

    /// <summary>An extension along <paramref name="nibbles"/> to <paramref name="child"/>, its hex-prefix flag nibble 5 rather than 1.</summary>
    private static byte[] HighFlagExtension(Witness witness, byte[] nibbles, byte[] child)
    {
        byte[] path = HexPrefix.ToBytes(nibbles, isLeaf: false);
        path[0] |= 0x40;
        return Rlp.Encode(Rlp.Encode(path), witness.Reference(child)).Bytes;
    }

    /// <summary>The same node with its short list header in the long form, which RLP does not allow for under 56 bytes.</summary>
    private static byte[] LongListHeader(byte[] node) => [0xf8, (byte)(node.Length - 1), .. node.AsSpan(1)];

    /// <summary>A sealed leaf decoded from RLP.</summary>
    private static TrieNode DecodedLeaf(byte[] key)
    {
        TreePath path = TreePath.Empty;
        byte[] rlp = TrieNodeFactory.CreateLeaf(key, new CappedArray<byte>([1])).RlpEncode(NullTrieNodeResolver.Instance, ref path).AsSpan().ToArray();
        TrieNode leaf = new(NodeType.Unknown, rlp);
        leaf.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        return leaf;
    }

    private static byte[] RawKey(byte seed)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 37 + seed * 101 + 5);
        return key;
    }

    private static Hash256 FakeHash(int id)
    {
        byte[] hash = new byte[Hash256.Size];
        BitConverter.TryWriteBytes(hash, id);
        hash[^1] = 0xee;
        return new Hash256(hash);
    }

    /// <summary>Witness nodes keyed by made-up hashes, referenced as a trie does: embedded when shorter than a hash.</summary>
    private sealed class Witness
    {
        private readonly NodeStorage _storage = new(new MemDb(), INodeStorage.KeyScheme.Hash);
        private readonly Dictionary<Hash256, byte[]> _hashed = [];
        private int _count;

        public PatriciaTree Tree(byte[] root) => new(new RawScopedTrieStore(_storage), NullLogManager.Instance) { RootHash = HashOf(root) };

        public byte[] Leaf(byte[] nibbles) => Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(nibbles, isLeaf: true)), Rlp.Encode(new byte[] { 1 })).Bytes;

        public byte[] Extension(byte[] nibbles, byte[] child) => ExtensionOfReference(nibbles, Reference(child));

        public byte[] ExtensionOfReference(byte[] nibbles, Rlp child) => Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(nibbles, isLeaf: false)), child).Bytes;

        public byte[] Branch(params (int Slot, byte[] Child)[] children) => Branch([], children);

        public byte[] Branch(byte[] value, params (int Slot, byte[] Child)[] children) =>
            BranchOfReferences(Rlp.Encode(value), Array.ConvertAll(children, c => (c.Slot, Reference(c.Child))));

        /// <summary>A node referenced by hash whatever its length.</summary>
        public byte[] Hashed(byte[] node)
        {
            _hashed[HashOf(node)] = node;
            return node;
        }

        /// <summary>A parent's reference to <paramref name="child"/>: the child itself when shorter than a hash.</summary>
        public Rlp Reference(byte[] child) => child.Length < Hash256.Size && !_hashed.ContainsValue(child) ? new Rlp(child) : Rlp.Encode(HashOf(child).Bytes);

        public static byte[] BranchOfReferences(Rlp value, params (int Slot, Rlp Reference)[] children)
        {
            Rlp[] items = new Rlp[17];
            Array.Fill(items, Rlp.OfEmptyByteArray);
            foreach ((int slot, Rlp reference) in children) items[slot] = reference;
            items[16] = value;
            return Rlp.Encode(items).Bytes;
        }

        private Hash256 HashOf(byte[] node)
        {
            foreach ((Hash256 hash, byte[] stored) in _hashed)
            {
                if (ReferenceEquals(stored, node)) return hash;
            }

            Hash256 newHash = FakeHash(++_count);
            _hashed[newHash] = node;
            _storage.Set(null, TreePath.Empty, newHash.ValueHash256, node);
            return newHash;
        }
    }
}
