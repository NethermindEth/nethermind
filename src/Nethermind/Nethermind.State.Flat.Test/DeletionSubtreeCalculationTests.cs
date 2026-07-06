// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.State.Flat.Sync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class DeletionSubtreeCalculationTests
{
    private static readonly byte[] DummyValue = new byte[33];   // >= 32 bytes -> hash (non-inline) child
    private static readonly byte[] InlineValue = new byte[5];   // small -> inline child

    private static byte[] Nibbles(string hex) =>
        hex.Select(c => (byte)(c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0')).ToArray();

    private static TrieNode Branch(ushort childBitset, byte[] childValue)
    {
        TrieNode branch = TrieNodeFactory.CreateBranch();
        for (int i = 0; i < 16; i++)
            if ((childBitset & (1 << i)) != 0)
                branch[i] = TrieNodeFactory.CreateLeaf([0], childValue);
        return branch;
    }

    private static TrieNode Leaf(string hexKey) => TrieNodeFactory.CreateLeaf(Nibbles(hexKey), DummyValue);
    private static TrieNode Extension(string hexKey) => TrieNodeFactory.CreateExtension(Nibbles(hexKey), TrieNodeFactory.CreateBranch());

    private static string[] Siblings(string prefix, int except) =>
        Enumerable.Range(0, 16).Where(c => c != except).Select(c => prefix + c.ToString("x")).ToArray();

    private static IEnumerable<TestCaseData> TestCases()
    {
        // Leaf: whole subtree rooted at path.
        yield return new TestCaseData("ab", Leaf("50000000000000000000000000000000000000000000000000000000000"), null,
            new[] { "ab" }).SetName("Leaf: deletes the whole subtree rooted at path");
        yield return new TestCaseData("ab", Leaf("5"), Leaf("5"),
            Array.Empty<string>()).SetName("Leaf->Leaf same key: nothing");

        // Branch (existing null): one root per null child.
        yield return new TestCaseData("ab", Branch(0b1111_1111_1111_1000, DummyValue), null,
            new[] { "ab0", "ab1", "ab2" }).SetName("Branch: null children 0-2 -> three roots");
        yield return new TestCaseData("ab", Branch(0b0011_1111_1111_1100, DummyValue), null,
            new[] { "ab0", "ab1", "abe", "abf" }).SetName("Branch: two null groups -> four roots");

        // Branch -> Branch: only where existing had a child and new doesn't.
        yield return new TestCaseData("ab", Branch(0b0000_0000_0000_0001, DummyValue), Branch(0b0000_0000_0000_0011, DummyValue),
            new[] { "ab1" }).SetName("Branch->Branch: existing(0,1) new(0) deletes child 1");
        yield return new TestCaseData("ab", Branch(0b0000_0000_0010_0000, DummyValue), Branch(0b0000_0000_0010_0000, DummyValue),
            Array.Empty<string>()).SetName("Branch->Branch: same structure -> nothing");
        // Inline new over hash existing still deletes; hash new over inline existing does not (child handles it).
        yield return new TestCaseData("ab", Branch(0b0000_0000_0000_1001, InlineValue), Branch(0b0000_0000_0000_0011, DummyValue),
            new[] { "ab0", "ab1" }).SetName("Branch(inline)->Branch(hash): inline at 0 and null at 1 delete");
        yield return new TestCaseData("ab", Branch(0b0000_0000_0000_1001, DummyValue), Branch(0b0000_0000_0000_0011, InlineValue),
            new[] { "ab1" }).SetName("Branch(hash)->Branch(inline): hash at 0 not deleted (child handles), null at 1 deletes");

        // Extension: sibling subtrees at each level of the key.
        yield return new TestCaseData("ab", Extension("5"), null,
            Siblings("ab", 5)).SetName("Extension key '5': 15 sibling roots at level 0");
        yield return new TestCaseData("ab", Extension("56"), null,
            Siblings("ab", 5).Concat(Siblings("ab5", 6)).ToArray()).SetName("Extension key '56': siblings at both levels");
        yield return new TestCaseData("ab", Extension("56"), Extension("56"),
            Array.Empty<string>()).SetName("Extension->Extension same key: nothing");

        // Other->Branch: only the existing node's own child nibble is considered for deletion.
        yield return new TestCaseData("ab", Branch(0b0000_0000_0010_0000, DummyValue), Leaf("50000000000000000000000000000000000000000000000000000000000"),
            Array.Empty<string>()).SetName("Leaf->Branch: existing child 5 kept (new has hash at 5)");
        yield return new TestCaseData("ab", Branch(0b0000_0000_0100_0000, DummyValue), Leaf("50000000000000000000000000000000000000000000000000000000000"),
            new[] { "ab5" }).SetName("Leaf->Branch: new null at existing child 5 deletes ab5");
    }

    [TestCaseSource(nameof(TestCases))]
    public void ComputeDeletionSubtrees_ReturnsExpectedRoots(string hexPath, TrieNode node, TrieNode? existingNode, string[] expectedRoots)
    {
        TreePath path = TreePath.FromHexString(hexPath);
        node.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        existingNode?.ResolveKey(NullTrieNodeResolver.Instance, ref path);

        path = TreePath.FromHexString(hexPath);
        List<TreePath> subtrees = [];
        FlatTreeSyncStore.ComputeDeletionSubtrees(path, node, existingNode, subtrees);

        TreePath[] expected = expectedRoots.Select(TreePath.FromHexString).ToArray();
        Assert.That(subtrees, Is.EquivalentTo(expected));
    }
}
