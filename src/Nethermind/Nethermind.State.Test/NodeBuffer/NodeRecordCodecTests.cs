// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test.NodeBuffer;

internal class NodeRecordCodecTests
{
    private const string ShortLeaf = "c482201205";
    // A branch of two leaves with one key nibble and a one-byte value, short enough to be embedded, which keccak keys only theoretically make.
    private const string EmbeddedBranch = "d5" + "c23101" + "c23201" + "808080808080808080808080808080";

    private static string HashItem(char digit) => "a0" + new string(digit, 64);

    private static string Repeat(string hex, int count) => string.Concat(Enumerable.Repeat(hex, count));

    private static IEnumerable<TestCaseData> RecordCases()
    {
        yield return new TestCaseData(ShortLeaf, NodeRecordKind.Leaf, (ushort)0).SetName("leaf even key, 1-byte value, root under 32 B");
        yield return new TestCaseData("ec823123a8" + Repeat("ab", 40), NodeRecordKind.Leaf, (ushort)0).SetName("leaf odd key, long value");
        yield return new TestCaseData("f869a031" + Repeat("11", 31) + "b846" + Repeat("cd", 70), NodeRecordKind.Leaf, (ushort)0).SetName("leaf, f8 header");
        yield return new TestCaseData("e215" + HashItem('2'), NodeRecordKind.Extension, (ushort)1).SetName("extension, hash child");
        yield return new TestCaseData("c8820001" + ShortLeaf, NodeRecordKind.Extension, (ushort)0).SetName("extension, inline child");
        yield return new TestCaseData("f871" + HashItem('3') + Repeat("80", 4) + HashItem('4') + Repeat("80", 9) + HashItem('5') + "80",
            NodeRecordKind.HashedBranch, (ushort)0x8021).SetName("hashed branch, f8 header");
        yield return new TestCaseData("f90211" + string.Concat("0123456789abcdef".Select(HashItem)) + "80",
            NodeRecordKind.HashedBranch, (ushort)0xFFFF).SetName("hashed branch, f9 header");
        yield return new TestCaseData("f5" + ShortLeaf + HashItem('7') + Repeat("80", 15), NodeRecordKind.InlineBranch, (ushort)0x0002)
            .SetName("inline branch, inline child at 0");
        yield return new TestCaseData("f5" + Repeat("80", 3) + HashItem('8') + Repeat("80", 11) + ShortLeaf + "80", NodeRecordKind.InlineBranch, (ushort)0x0008)
            .SetName("inline branch, inline child at 15");
        yield return new TestCaseData("d710" + EmbeddedBranch, NodeRecordKind.Extension, (ushort)0).SetName("extension, embedded branch child");
        yield return new TestCaseData("f848" + "d710" + EmbeddedBranch + HashItem('9') + Repeat("80", 15), NodeRecordKind.InlineBranch, (ushort)0x0002)
            .SetName("inline branch, embedded extension child over an embedded branch");
    }

    [TestCaseSource(nameof(RecordCases))]
    public void Record_round_trips_rlp(string rlpHex, NodeRecordKind expectedKind, ushort expectedMask)
    {
        byte[] rlp = Bytes.FromHexString(rlpHex);
        Assert.That(NodeRecordCodec.TryDecode(rlp, out NodeRecordKind kind, out ushort hashMask), Is.True);

        byte[] buffer = new byte[NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length)];
        NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, ValueKeccak.Compute(rlp), nodeKeyHash: 0, buffer);
        NodeRecord record = new(buffer.AsMemory(), 0);

        TrieNode node = new(NodeType.Unknown, rlp);
        node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        TreePath rootPath = TreePath.Empty;
        Hash256 expectedHash = node.GenerateKey(NullTrieNodeResolver.Instance, ref rootPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kind, Is.EqualTo(expectedKind));
            Assert.That(hashMask, Is.EqualTo(expectedMask));
            Assert.That(record.Span.Length, Is.EqualTo(buffer.Length));
            Assert.That(record.Hash, Is.EqualTo(expectedHash.ValueHash256));
            Assert.That(record.View.Hash, Is.EqualTo(expectedHash.ValueHash256));
            Assert.That(record.Rlp.ToArray(), Is.EqualTo(rlp));
            Assert.That(record.ToTrieNode().FullRlp.ToArray(), Is.EqualTo(rlp));
            Assert.That(ReEncode(record.View), Is.EqualTo(rlp));
            AssertChildren(record.View, node);
            AssertKeyAndValue(record.View, node);
            AssertChildHashes(record.View, node);
        }
    }

    [TestCase("d710" + "d5c23101c23201" + "8080808080808080808080808080" + "01", TestName = "embedded branch value")]
    [TestCase("f851"
        + "a01111111111111111111111111111111111111111111111111111111111111111"
        + "a02222222222222222222222222222222222222222222222222222222222222222"
        + "8080808080808080808080808080" + "01", TestName = "branch value")]
    [TestCase("e215a0" + "22222222222222222222222222222222222222222222222222222222222222", TestName = "truncated")]
    public void Rejects_invalid_rlp(string rlpHex) =>
        Assert.That(NodeRecordCodec.TryDecode(Bytes.FromHexString(rlpHex), out _, out _), Is.False);

    // Trie nodes are found by hash, so the codec reads them without the checks of the canonical form.
    private static IEnumerable<TestCaseData> NonCanonicalCases()
    {
        const string leafWithPrefixedByte = "c58220128105";
        yield return new TestCaseData(leafWithPrefixedByte, NodeRecordKind.Leaf, (ushort)0).SetName("leaf, single byte value with a prefix");
        yield return new TestCaseData("f6" + leafWithPrefixedByte + HashItem('7') + Repeat("80", 15), NodeRecordKind.InlineBranch, (ushort)0x0002)
            .SetName("inline branch, inline child with a prefixed byte");
    }

    [TestCaseSource(nameof(NonCanonicalCases))]
    public void Decodes_non_canonical_rlp(string rlpHex, NodeRecordKind expectedKind, ushort expectedMask)
    {
        bool decoded = NodeRecordCodec.TryDecode(Bytes.FromHexString(rlpHex), out NodeRecordKind kind, out ushort hashMask);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True);
            Assert.That(kind, Is.EqualTo(expectedKind));
            Assert.That(hashMask, Is.EqualTo(expectedMask));
        }
    }

    [Test]
    public void ChildRef_is_48_bytes() => Assert.That(Unsafe.SizeOf<ChildRef>(), Is.EqualTo(48));

    private static byte[] ReEncode(NodeRecordRef view)
    {
        byte[] key = new byte[view.KeyLength(0)];
        view.CopyKey(0, key);
        byte[] encoded;
        int written;
        switch (view.Kind)
        {
            case NodeRecordKind.Leaf:
                encoded = new byte[NodeRlp.LeafLength(key, view.LeafValue)];
                written = NodeRlp.WriteLeaf(key, view.LeafValue, encoded);
                break;
            case NodeRecordKind.Extension:
                ChildRef child = view.Child(0);
                encoded = new byte[NodeRlp.ExtensionLength(key, child)];
                written = NodeRlp.WriteExtension(key, child, encoded);
                break;
            default:
                ChildRef[] children = new ChildRef[16];
                view.CopyChildren(children);
                encoded = new byte[NodeRlp.BranchLength(children)];
                written = NodeRlp.WriteBranch(children, encoded);
                break;
        }

        return encoded[..written];
    }

    private static void AssertChildren(NodeRecordRef view, TrieNode node)
    {
        int childCount = node.IsBranch ? 16 : node.IsExtension ? 1 : 0;
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < childCount; i++)
            {
                ChildRef child = view.Child(i);
                Assert.That(child.Pointer, Is.Zero, $"child {i} pointer");
                if (node.GetChildHashAsValueKeccak(i, out ValueHash256 childHash))
                {
                    Assert.That(child.Kind, Is.EqualTo(ChildRefKind.Hash), $"child {i} kind");
                    Assert.That(child.Data, Is.EqualTo(childHash), $"child {i} hash");
                }
                else if (node.GetInlineNodeRlp(i) is { } inlineRlp)
                {
                    Assert.That(child.Kind, Is.EqualTo(ChildRefKind.Inline), $"child {i} kind");
                    Assert.That(child.InlineRlp.ToArray(), Is.EqualTo(inlineRlp), $"child {i} inline RLP");
                }
                else
                {
                    Assert.That(child.Kind, Is.EqualTo(ChildRefKind.Empty), $"child {i} kind");
                }
            }
        }
    }

    private static void AssertKeyAndValue(NodeRecordRef view, TrieNode node)
    {
        if (node.IsBranch)
        {
            Assert.That(view.KeyLength(0), Is.Zero, "branch key length");
            return;
        }

        byte[] key = new byte[view.KeyLength(0)];
        view.CopyKey(0, key);
        Assert.That(key, Is.EqualTo(node.Key), "key");

        if (key.Length > 0)
        {
            byte[] keyAfterFirst = new byte[view.KeyLength(1)];
            view.CopyKey(1, keyAfterFirst);
            Assert.That(keyAfterFirst, Is.EqualTo(node.Key[1..]), "key after the first nibble");
        }

        if (node.IsLeaf) Assert.That(view.LeafValue.ToArray(), Is.EqualTo(node.Value.ToArray()), "value");
    }

    private static void AssertChildHashes(NodeRecordRef view, TrieNode node)
    {
        int childCount = node.IsBranch ? 16 : node.IsExtension ? 1 : 0;
        // Past both ends of the children too: index 32 wraps a 32-bit shift back to child 0.
        for (int i = -1; i <= 32; i++)
        {
            ValueHash256 expectedHash = default;
            bool hasHash = (uint)i < childCount && node.GetChildHashAsValueKeccak(i, out expectedHash);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(view.TryGetChildHash(i, out ValueHash256 hash), Is.EqualTo(hasHash), $"child {i} has a hash");
                Assert.That(hash, Is.EqualTo(hasHash ? expectedHash : default), $"child {i} hash");
            }
        }
    }
}
