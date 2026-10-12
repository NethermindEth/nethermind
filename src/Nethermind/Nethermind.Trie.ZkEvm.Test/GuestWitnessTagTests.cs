// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>The witness tag the guest's <see cref="TrieNode"/> keeps in the upper half of its RLP length word.</summary>
/// <remarks>
/// Read and set through reflection: the tag is only ever consumed by the resumed keccak, whose permutation is a
/// zkVM precompile no host process can call.
/// </remarks>
// The tag a node storage notes is process-wide.
[NonParallelizable]
public class GuestWitnessTagTests
{
    private const int Tag = 5;

    [TearDown]
    public void TearDown() => NoteWitnessNodeLoaded(0);

    [Test]
    public void Loaded_node_hands_its_rlp_and_the_tag_to_the_re_encode_once_writable([Values] bool unsealInPlace)
    {
        byte[] rlp = FullBranchRlp();
        TrieNode node = ResolveTagged(rlp);
        TrieNode writable = unsealInPlace ? node.Unseal() : node.Clone();
        object previous = typeof(TrieNode).GetMethod("ReadPreviousRlp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(writable, null)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.IsBranch);
            Assert.That(TagOf(node), Is.EqualTo(Tag));
            Assert.That(writable.FullRlp.AsSpan().ToArray(), Is.EqualTo(rlp));
            Assert.That(previous.GetType().GetField("Array")!.GetValue(previous), Is.SameAs(rlp));
            Assert.That(previous.GetType().GetField("Tag")!.GetValue(previous), Is.EqualTo((nint)Tag));
        }
    }

    [Test]
    public void Rlp_write_drops_the_tag()
    {
        byte[] rlp = FullBranchRlp();
        TrieNode node = ResolveTagged(rlp);

        node.WriteRlp(new CappedArray<byte>(rlp));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TagOf(node), Is.Zero);
            Assert.That(node.FullRlp.Length, Is.EqualTo(rlp.Length));
        }
    }

    private static TrieNode ResolveTagged(byte[] rlp)
    {
        TrieNode node = new(NodeType.Unknown, new Hash256(rlp.AsSpan(4, Hash256.Size)));
        NoteWitnessNodeLoaded(Tag);
        node.ResolveNode(new FixedRlpResolver(rlp), TreePath.Empty);
        return node;
    }

    private static byte[] FullBranchRlp()
    {
        byte[] rlp = new byte[3 + TrieNode.BranchesCount * (Hash256.Size + 1) + 1];
        rlp[0] = 0xf9;
        rlp[1] = 0x02;
        rlp[2] = 0x11;
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            int offset = 3 + i * (Hash256.Size + 1);
            rlp[offset] = 0xa0;
            rlp[offset + 1] = (byte)(i + 1);
        }

        rlp[^1] = 0x80;
        return rlp;
    }

    private static void NoteWitnessNodeLoaded(nint tag) =>
        typeof(KeccakHash).GetMethod("NoteWitnessNodeLoaded", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [tag]);

    private static ulong TagOf(TrieNode node) =>
        (ulong)typeof(TrieNode).GetField("_rlpSeqAndLength", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)! >> 32;

    private sealed class FixedRlpResolver(byte[] rlp) : ITrieNodeResolver
    {
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);
        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => rlp;
        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => rlp;
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => this;
        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.Hash;
    }
}
