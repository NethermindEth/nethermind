// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Sync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class DeletionRangeCalculationTests
{
    private static readonly byte[] DummyValue = new byte[33];
    private static readonly byte[] InlineValue = new byte[5]; // Small enough for inline (RLP < 32 bytes)

    private static IEnumerable<TestCaseData> ComputeDeletionRangesTestCases()
    {
        // Note to AI: please keep the deletion range to left so that its easy to read.

        // === Helper functions ===
        static byte[] NibblesFromHexString(string hex) =>
            hex.Select(c => (byte)(c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0')).ToArray();

        TrieNode CreateBranchWithChildren(ushort childBitset)
        {
            TrieNode branch = TrieNodeFactory.CreateBranch();
            for (int i = 0; i < 16; i++)
                if ((childBitset & (1 << i)) != 0)
                    branch[i] = TrieNodeFactory.CreateLeaf([0], DummyValue);
            return branch;
        }

        TrieNode CreateBranchWithInlineChildren(ushort childBitset)
        {
            TrieNode branch = TrieNodeFactory.CreateBranch();
            for (int i = 0; i < 16; i++)
                if ((childBitset & (1 << i)) != 0)
                    branch[i] = TrieNodeFactory.CreateLeaf([0], InlineValue);
            return branch;
        }

        TrieNode CreateLeaf(string hexKey) =>
            TrieNodeFactory.CreateLeaf(NibblesFromHexString(hexKey), DummyValue);

        TrieNode CreateExtension(string hexKey) =>
            TrieNodeFactory.CreateExtension(NibblesFromHexString(hexKey), TrieNodeFactory.CreateBranch());

        // === Branch tests ===

        // With path prefix "ab", children 0, 1, 2 null (consecutive)
        yield return new TestCaseData(
            "ab",
            CreateBranchWithChildren(0b1111_1111_1111_1000),
            null,
            new (string, string)[]
            {
                ("0xab00000000000000000000000000000000000000000000000000000000000000", "0xab2fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch: with path prefix, consecutive null 0-2 yield 1 range");

        // Children 3, 4, 5 are null with path "abcde"
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b1111_1111_1100_0111),
            null,
            new (string, string)[]
            {
                ("0xabcde30000000000000000000000000000000000000000000000000000000000", "0xabcde5ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch: with 5-nibble path, consecutive null 3-5 yield 1 range");

        // Children 0, 1 and 14, 15 null with path "abcde"
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b0011_1111_1111_1100),
            null,
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde1ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xabcdee0000000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch: with 5-nibble path, two null groups yield 2 ranges");

        // Single null child 5 with path "abcde"
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b1111_1111_1101_1111),
            null,
            new (string, string)[]
            {
                ("0xabcde50000000000000000000000000000000000000000000000000000000000", "0xabcde5ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch: with 5-nibble path, single null child yields 1 range");

        // === Leaf tests ===
        // Leaf logic changed: now just deletes the whole subtree range (path.ToLowerBound to path.ToUpperBound)

        // Leaf with path prefix "ab" - deletes ab00...00 to abff...ff
        yield return new TestCaseData(
            "ab",
            CreateLeaf("500000000000000000000000000000000000000000000000000000000000"),
            null,
            new (string, string)[]
            {
                ("0xab00000000000000000000000000000000000000000000000000000000000000", "0xabffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Leaf: with path prefix deletes subtree range");

        // Leaf with path "abcde" - deletes abcde00...00 to abcdef...ff
        yield return new TestCaseData(
            "abcde",
            CreateLeaf("00000000000000000000000000000000000000000000000000000000000"),
            null,
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Leaf: with 5-nibble path deletes subtree range");

        // Leaf with path "abcde" (different key, same result)
        yield return new TestCaseData(
            "abcde",
            CreateLeaf("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            null,
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Leaf: with 5-nibble path deletes subtree range regardless of key");

        // Leaf with path "abcde" with middle key (same result)
        yield return new TestCaseData(
            "abcde",
            CreateLeaf("80000000000000000000000000000000000000000000000000000000000"),
            null,
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Leaf: with 5-nibble path deletes subtree range regardless of key");

        // === Extension tests ===

        // Extension at start with path "abcde"
        yield return new TestCaseData(
            "abcde",
            CreateExtension("0000"),
            null,
            new (string, string)[]
            {
                ("0xabcde00010000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Extension: with 5-nibble path, at start yields only after gap");

        // Extension at end with path "abcde" - only yields before gap (no overflow into next prefix range)
        yield return new TestCaseData(
            "abcde",
            CreateExtension("ffff"),
            null,
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcdefffefffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Extension: with 5-nibble path, at end yields only before gap");

        // Extension in middle with path "abcde"
        yield return new TestCaseData(
            "abcde",
            CreateExtension("8"),
            null,
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde7ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xabcde90000000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Extension: with 5-nibble path, in middle yields both gaps");

        // Extension with longer key "12345" with path "abcde"
        yield return new TestCaseData(
            "abcde",
            CreateExtension("12345"),
            null,
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde12344ffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xabcde12346000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Extension: with 5-nibble path, longer key creates narrow survival zone");

        // === Optimized deletion tests (with existing nodes) ===

        // Branch to Branch: Only deleted removed children
        // Existing: children at 0, 1 (null at 2-15); New: children at 0, 3 (null at 1-2, 4-15)
        // Only position 1 needs deletion (went from non-null to null)
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b0000_0000_0000_1001),
            CreateBranchWithChildren(0b0000_0000_0000_0011),
            new (string, string)[]
            {
                ("0xabcde10000000000000000000000000000000000000000000000000000000000", "0xabcde1ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch→Branch: Existing(0,1) to New(0,3) deletes only child 1");

        // Same children structure - no deletions
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b0000_0000_0010_0000),
            CreateBranchWithChildren(0b0000_0000_0010_0000),
            Array.Empty<(string, string)>()
        ).SetName("Branch→Branch: Same structure yields no deletions");

        // All children to single child - deletes 0-4, 6-15
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b0000_0000_0010_0000),
            CreateBranchWithChildren(0b1111_1111_1111_1111),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde4ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xabcde60000000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch→Branch: All children to single child deletes 0-4, 6-15");

        // Branch(inline)→Branch(inline): Both have inline children at shared positions
        // newNode: inline at 0, 3 (bitset 0b1001); existingNode: inline at 0, 1 (bitset 0b0011)
        // Position 0: newNode has inline (no hash), existing has inline → DELETE
        // Position 1: newNode is null, existing has inline → DELETE
        // Position 3: newNode has inline, existing is null → no delete
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithInlineChildren(0b0000_0000_0000_1001),
            CreateBranchWithInlineChildren(0b0000_0000_0000_0011),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde1ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch(inline)→Branch(inline): Inline children at shared positions trigger deletion");

        // Branch(hash)→Branch(inline): Existing has hash refs, new has inline children
        // newNode: inline at 0, 3 (bitset 0b1001); existingNode: hash ref at 0, 1 (bitset 0b0011)
        // Position 0: newNode has inline (no hash), existing has hash → DELETE
        // Position 1: newNode is null, existing has hash → DELETE
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithInlineChildren(0b0000_0000_0000_1001),
            CreateBranchWithChildren(0b0000_0000_0000_0011),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde1ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch(hash)→Branch(inline): Inline new replaces hash existing");

        // === Bottom-up sync: hash ref children handle their own deletion ===

        // Branch(inline)→Branch(hash): Existing has inline, new has hash refs
        // newNode: hash at 0, 3; existingNode: inline at 0, 1
        // Position 0: newNode has hash, existing has inline → NO DELETE (child handles)
        // Position 1: newNode is null, existing has inline → DELETE
        // Position 3: newNode has hash, existing is null → no delete
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b0000_0000_0000_1001),
            CreateBranchWithInlineChildren(0b0000_0000_0000_0011),
            new (string, string)[]
            {
                ("0xabcde10000000000000000000000000000000000000000000000000000000000", "0xabcde1ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch(inline)→Branch(hash): inline→hash at pos 0 NOT deleted (child handles), inline→null at pos 1 deleted");

        // Branch(hash)→Branch(partial null): Existing has hash refs, new removes some children
        // newNode: hash at 3 only; existingNode: hash at 0, 1, 3
        // Position 0: newNode is null, existing has hash → DELETE (no child to handle)
        // Position 1: newNode is null, existing has hash → DELETE
        // Position 3: newNode has hash, existing has hash → NO DELETE
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b0000_0000_0000_1000),
            CreateBranchWithChildren(0b0000_0000_0000_1011),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde1ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch(hash)→Branch(hash): hash→null at pos 0,1 deleted (no child), hash→hash at pos 3 NOT deleted");

        // Branch(inline)→Branch(partial hash): Mixed transitions
        // newNode: hash at 3 only; existingNode: inline at 0, 1, 3
        // Position 0: newNode is null, existing has inline → DELETE
        // Position 1: newNode is null, existing has inline → DELETE
        // Position 3: newNode has hash, existing has inline → NO DELETE (child handles)
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b0000_0000_0000_1000),
            CreateBranchWithInlineChildren(0b0000_0000_0000_1011),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde1ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Branch(inline)→Branch(hash): inline→null at pos 0,1 deleted, inline→hash at pos 3 NOT deleted (child handles)");

        // Same type, same key: No deletion - Leaf → Leaf with same key
        yield return new TestCaseData(
            "abcde",
            CreateLeaf("50000000000000000000000000000000000000000000000000000000000"),
            CreateLeaf("50000000000000000000000000000000000000000000000000000000000"),
            Array.Empty<(string, string)>()
        ).SetName("Leaf→Leaf: Same key yields no deletions");

        // Extension → Extension with same key
        yield return new TestCaseData(
            "abcde",
            CreateExtension("56"),
            CreateExtension("56"),
            Array.Empty<(string, string)>()
        ).SetName("Extension→Extension: Same key yields no deletions");

        // Cross-type transitions: Leaf at 5abc... → Branch with child 5 non-null
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b0000_0000_0010_0000),
            CreateLeaf("5abc0000000000000000000000000000000000000000000000000000000"),
            Array.Empty<(string, string)>()
        ).SetName("Leaf→Branch: Optimized deletion - only childNibble considered, child 5 has hash ref");

        // Leaf at 5abc... → Branch with child 6 non-null
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithChildren(0b0000_0000_0100_0000),
            CreateLeaf("5abc0000000000000000000000000000000000000000000000000000000"),
            new (string, string)[]
            {
                ("0xabcde50000000000000000000000000000000000000000000000000000000000", "0xabcde5ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Leaf→Branch: Optimized deletion - childNibble=5, new null at 5 → delete 5 only");

        // Extension("56") → Leaf at 5678...: Leaf just deletes its whole range
        yield return new TestCaseData(
            "abcde",
            CreateLeaf("56780000000000000000000000000000000000000000000000000000000"),
            CreateExtension("56"),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetName("Extension→Leaf: Leaf deletes entire range");
    }

    [TestCaseSource(nameof(ComputeDeletionRangesTestCases))]
    public void ComputeDeletionRanges_ReturnsExpectedRanges(string hexPath, TrieNode node, TrieNode? existingNode, (string From, string To)[] expectedRanges)
    {
        // Note to AI: You are not allowed to change this method.

        TreePath path = TreePath.FromHexString(hexPath);
        node.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        existingNode?.ResolveKey(NullTrieNodeResolver.Instance, ref path);

        path = TreePath.FromHexString(hexPath);
        RefList16<FlatTreeSyncStore.DeletionRange> ranges = new();
        FlatTreeSyncStore.ComputeDeletionRanges(path, node, existingNode, ref ranges);

        Assert.That(ranges.Count, Is.EqualTo(expectedRanges.Length));
        for (int i = 0; i < expectedRanges.Length; i++)
        {
            Assert.That(expectedRanges[i].From.Length, Is.EqualTo(66)); // 64 hex chars + "0x" prefix
            Assert.That(expectedRanges[i].To.Length, Is.EqualTo(66));
            Assert.That(ranges[i].From, Is.EqualTo(new ValueHash256(expectedRanges[i].From)), $"Range[{i}].From mismatch");
            Assert.That(ranges[i].To, Is.EqualTo(new ValueHash256(expectedRanges[i].To)), $"Range[{i}].To mismatch");
        }
    }

}
