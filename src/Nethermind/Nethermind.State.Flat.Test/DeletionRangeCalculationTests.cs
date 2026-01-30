// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Sync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class DeletionRangeCalculationTests
{
    private static readonly byte[] DummyValue = [0x01];

    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000001", Description = "Normal increment")]
    [TestCase("0x00000000000000000000000000000000000000000000000000000000000000ff", "0x0000000000000000000000000000000000000000000000000000000000000100", Description = "Byte boundary carry")]
    [TestCase("0x000000000000000000000000000000000000000000000000000000000000ffff", "0x0000000000000000000000000000000000000000000000000000000000010000", Description = "Multiple byte carry")]
    [TestCase("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", Description = "Overflow returns max")]
    public void IncrementPath_ReturnsExpected(string inputHex, string expectedHex)
    {
        ValueHash256 path = new(inputHex);
        ValueHash256 result = FlatTreeSyncStore.IncrementPath(path);
        Assert.That(result, Is.EqualTo(new ValueHash256(expectedHex)));
    }

    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", "0x0000000000000000000000000000000000000000000000000000000000000000", Description = "Normal decrement")]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000100", "0x00000000000000000000000000000000000000000000000000000000000000ff", Description = "Byte boundary borrow")]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000010000", "0x000000000000000000000000000000000000000000000000000000000000ffff", Description = "Multiple byte borrow")]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x0000000000000000000000000000000000000000000000000000000000000000", Description = "Underflow returns zero")]
    public void DecrementPath_ReturnsExpected(string inputHex, string expectedHex)
    {
        ValueHash256 path = new(inputHex);
        ValueHash256 result = FlatTreeSyncStore.DecrementPath(path);
        Assert.That(result, Is.EqualTo(new ValueHash256(expectedHex)));
    }

    private static IEnumerable<TestCaseData> ComputeDeletionRangesTestCases()
    {
        // === Helper functions ===
        static byte[] NibblesFromHexString(string hex) =>
            hex.Select(c => (byte)(c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0')).ToArray();

        TrieNode CreateBranchWithNullChildren(params int[] nullIndices)
        {
            TrieNode branch = TrieNodeFactory.CreateBranch();
            for (int i = 0; i < 16; i++)
                if (!nullIndices.Contains(i))
                    branch[i] = TrieNodeFactory.CreateLeaf([0], DummyValue);
            return branch;
        }

        // === Branch tests ===

        // Children 3, 4, 5 are null (consecutive) - yields 1 combined range
        yield return new TestCaseData(
            "",
            CreateBranchWithNullChildren(3, 4, 5),
            new (string, string)[]
            {
                ("0x3000000000000000000000000000000000000000000000000000000000000000", "0x5fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Branch: consecutive null children 3-5 yield 1 combined range");

        // Children 0, 1 and 14, 15 are null (two consecutive groups) - yields 2 ranges
        yield return new TestCaseData(
            "",
            CreateBranchWithNullChildren(0, 1, 14, 15),
            new (string, string)[]
            {
                ("0x0000000000000000000000000000000000000000000000000000000000000000", "0x1fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xe000000000000000000000000000000000000000000000000000000000000000", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Branch: two consecutive null groups (0-1, 14-15) yield 2 ranges");

        // Single null child 5 - yields 1 range (no merging needed)
        yield return new TestCaseData(
            "",
            CreateBranchWithNullChildren(5),
            new (string, string)[]
            {
                ("0x5000000000000000000000000000000000000000000000000000000000000000", "0x5fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Branch: single null child yields 1 range");

        // With path prefix "ab", children 0, 1, 2 null (consecutive)
        yield return new TestCaseData(
            "ab",
            CreateBranchWithNullChildren(0, 1, 2),
            new (string, string)[]
            {
                ("0xab00000000000000000000000000000000000000000000000000000000000000", "0xab2fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Branch: with path prefix, consecutive null 0-2 yield 1 range");

        // Children 3, 4, 5 are null with path "abcde"
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithNullChildren(3, 4, 5),
            new (string, string)[]
            {
                ("0xabcde30000000000000000000000000000000000000000000000000000000000", "0xabcde5ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Branch: with 5-nibble path, consecutive null 3-5 yield 1 range");

        // Children 0, 1 and 14, 15 null with path "abcde"
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithNullChildren(0, 1, 14, 15),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde1ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xabcdee0000000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Branch: with 5-nibble path, two null groups yield 2 ranges");

        // Single null child 5 with path "abcde"
        yield return new TestCaseData(
            "abcde",
            CreateBranchWithNullChildren(5),
            new (string, string)[]
            {
                ("0xabcde50000000000000000000000000000000000000000000000000000000000", "0xabcde5ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Branch: with 5-nibble path, single null child yields 1 range");

        // === Leaf tests ===

        // Leaf at start (key = all zeros)
        yield return new TestCaseData(
            "",
            TrieNodeFactory.CreateLeaf(NibblesFromHexString("0000000000000000000000000000000000000000000000000000000000000000"), DummyValue),
            new (string, string)[]
            {
                ("0x0000000000000000000000000000000000000000000000000000000000000001", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Leaf: at start yields only after gap");

        // Leaf at end (key = all 0xF)
        yield return new TestCaseData(
            "",
            TrieNodeFactory.CreateLeaf(NibblesFromHexString("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), DummyValue),
            new (string, string)[]
            {
                ("0x0000000000000000000000000000000000000000000000000000000000000000", "0xfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe"),
                ("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Leaf: at end yields before gap and degenerate after gap");

        // Leaf in middle (key = 0x80...)
        yield return new TestCaseData(
            "",
            TrieNodeFactory.CreateLeaf(NibblesFromHexString("8000000000000000000000000000000000000000000000000000000000000000"), DummyValue),
            new (string, string)[]
            {
                ("0x0000000000000000000000000000000000000000000000000000000000000000", "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0x8000000000000000000000000000000000000000000000000000000000000001", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Leaf: in middle yields both gaps");

        // Leaf with path prefix "ab", key "5..."
        yield return new TestCaseData(
            "ab",
            TrieNodeFactory.CreateLeaf(NibblesFromHexString("500000000000000000000000000000000000000000000000000000000000"), DummyValue),
            new (string, string)[]
            {
                ("0xab00000000000000000000000000000000000000000000000000000000000000", "0xab4fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xab50000000000000000000000000000000000000000000000000000000000001", "0xabffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Leaf: with path prefix correctly pads ranges");

        // Leaf at start with path "abcde"
        yield return new TestCaseData(
            "abcde",
            TrieNodeFactory.CreateLeaf(NibblesFromHexString("00000000000000000000000000000000000000000000000000000000000"), DummyValue),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000001", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Leaf: with 5-nibble path, at start yields only after gap");

        // Leaf at end with path "abcde" - only yields before gap (no overflow into next prefix range)
        yield return new TestCaseData(
            "abcde",
            TrieNodeFactory.CreateLeaf(NibblesFromHexString("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), DummyValue),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcdeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe")
            }
        ).SetDescription("Leaf: with 5-nibble path, at end yields only before gap");

        // Leaf in middle with path "abcde"
        yield return new TestCaseData(
            "abcde",
            TrieNodeFactory.CreateLeaf(NibblesFromHexString("80000000000000000000000000000000000000000000000000000000000"), DummyValue),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde7ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xabcde80000000000000000000000000000000000000000000000000000000001", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Leaf: with 5-nibble path, in middle yields both gaps");

        // === Extension tests ===

        // Extension at start (key = 0000)
        yield return new TestCaseData(
            "",
            TrieNodeFactory.CreateExtension(NibblesFromHexString("0000"), TrieNodeFactory.CreateBranch()),
            new (string, string)[]
            {
                ("0x0001000000000000000000000000000000000000000000000000000000000000", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Extension: at start yields only after gap");

        // Extension at end (key = ffff)
        yield return new TestCaseData(
            "",
            TrieNodeFactory.CreateExtension(NibblesFromHexString("ffff"), TrieNodeFactory.CreateBranch()),
            new (string, string)[]
            {
                ("0x0000000000000000000000000000000000000000000000000000000000000000", "0xfffeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Extension: at end yields before gap and degenerate after gap");

        // Extension in middle (key = 8)
        yield return new TestCaseData(
            "",
            TrieNodeFactory.CreateExtension(NibblesFromHexString("8"), TrieNodeFactory.CreateBranch()),
            new (string, string)[]
            {
                ("0x0000000000000000000000000000000000000000000000000000000000000000", "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0x9000000000000000000000000000000000000000000000000000000000000000", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Extension: in middle yields both gaps");

        // Extension with longer key "12345" (5 nibbles) - narrow survival zone
        // Gaps: 0... to 12344fff..., 12346... to fff...
        yield return new TestCaseData(
            "",
            TrieNodeFactory.CreateExtension(NibblesFromHexString("12345"), TrieNodeFactory.CreateBranch()),
            new (string, string)[]
            {
                ("0x0000000000000000000000000000000000000000000000000000000000000000", "0x12344fffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0x1234600000000000000000000000000000000000000000000000000000000000", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Extension: longer key (5 nibbles) creates narrow survival zone");

        // Extension at start with path "abcde"
        yield return new TestCaseData(
            "abcde",
            TrieNodeFactory.CreateExtension(NibblesFromHexString("0000"), TrieNodeFactory.CreateBranch()),
            new (string, string)[]
            {
                ("0xabcde00010000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Extension: with 5-nibble path, at start yields only after gap");

        // Extension at end with path "abcde" - only yields before gap (no overflow into next prefix range)
        yield return new TestCaseData(
            "abcde",
            TrieNodeFactory.CreateExtension(NibblesFromHexString("ffff"), TrieNodeFactory.CreateBranch()),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcdefffefffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Extension: with 5-nibble path, at end yields only before gap");

        // Extension in middle with path "abcde"
        yield return new TestCaseData(
            "abcde",
            TrieNodeFactory.CreateExtension(NibblesFromHexString("8"), TrieNodeFactory.CreateBranch()),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde7ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xabcde90000000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Extension: with 5-nibble path, in middle yields both gaps");

        // Extension with longer key "12345" with path "abcde"
        yield return new TestCaseData(
            "abcde",
            TrieNodeFactory.CreateExtension(NibblesFromHexString("12345"), TrieNodeFactory.CreateBranch()),
            new (string, string)[]
            {
                ("0xabcde00000000000000000000000000000000000000000000000000000000000", "0xabcde12344ffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0xabcde12346000000000000000000000000000000000000000000000000000000", "0xabcdefffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            }
        ).SetDescription("Extension: with 5-nibble path, longer key creates narrow survival zone");

        // === Unknown node type ===
        yield return new TestCaseData(
            "",
            new TrieNode(NodeType.Unknown),
            Array.Empty<(string, string)>()
        ).SetDescription("Unknown node type yields empty");
    }

    [TestCaseSource(nameof(ComputeDeletionRangesTestCases))]
    public void ComputeDeletionRanges_ReturnsExpectedRanges(string hexPath, TrieNode node, (string From, string To)[] expectedRanges)
    {
        TreePath path = hexPath == "" ? TreePath.Empty : TreePath.FromHexString(hexPath);
        RefList16<FlatTreeSyncStore.DeletionRange> ranges = new();
        FlatTreeSyncStore.ComputeDeletionRanges(path, node, ref ranges);

        Assert.That(ranges.Count, Is.EqualTo(expectedRanges.Length));
        for (int i = 0; i < expectedRanges.Length; i++)
        {
            Assert.That(expectedRanges[i].From.Length, Is.EqualTo(66)); // 64 hex chars + "0x" prefix
            Assert.That(expectedRanges[i].To.Length, Is.EqualTo(66));
            Assert.That(ranges[i].From, Is.EqualTo(new ValueHash256(expectedRanges[i].From)), $"Range[{i}].From mismatch");
            Assert.That(ranges[i].To, Is.EqualTo(new ValueHash256(expectedRanges[i].To)), $"Range[{i}].To mismatch");
        }
    }

    /// <summary>
    /// Helper to check if a path falls within any of the deletion ranges.
    /// </summary>
    private static bool IsDeleted(FlatTreeSyncStore.DeletionRange[] ranges, string pathHex)
    {
        ValueHash256 path = new(pathHex);
        foreach (FlatTreeSyncStore.DeletionRange range in ranges)
        {
            if (path.CompareTo(range.From) >= 0 && path.CompareTo(range.To) <= 0)
                return true;
        }
        return false;
    }

    private static FlatTreeSyncStore.DeletionRange[] ComputeRanges(TreePath path, TrieNode node)
    {
        RefList16<FlatTreeSyncStore.DeletionRange> ranges = new();
        FlatTreeSyncStore.ComputeDeletionRanges(path, node, ref ranges);
        return ranges.AsSpan().ToArray();
    }

    /// <summary>
    /// Visualizes: OLD=Branch(children 0,3,a) → NEW=Extension("5")
    /// Extension gaps should delete old branch children at 0..., 3..., a...
    /// Only 5... range survives (covered by extension).
    /// </summary>
    [Test]
    public void TypeChange_BranchToExtension_DeletesOldBranchChildrenOutsideExtensionRange()
    {
        // NEW node: Extension with key "5" at root
        TrieNode newExtension = TrieNodeFactory.CreateExtension([0x5], TrieNodeFactory.CreateBranch());
        FlatTreeSyncStore.DeletionRange[] ranges = ComputeRanges(TreePath.Empty, newExtension);

        // Extension("5") gaps: 0... to 4fff..., 6... to fff...
        // Old branch had children at 0, 3, a → data at 0..., 3..., a...
        Assert.Multiple(() =>
        {
            Assert.That(IsDeleted(ranges, "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
                Is.True, "Old child 0 data should be deleted (in before gap)");
            Assert.That(IsDeleted(ranges, "0x3000000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Old child 3 data should be deleted (in before gap)");
            Assert.That(IsDeleted(ranges, "0xa000000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Old child a data should be deleted (in after gap)");

            // Data within extension's range survives (to be handled by child sync)
            Assert.That(IsDeleted(ranges, "0x5000000000000000000000000000000000000000000000000000000000000000"),
                Is.False, "Data at 5... survives (within extension range)");
            Assert.That(IsDeleted(ranges, "0x5fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Is.False, "Data at 5fff... survives (within extension range)");
        });
    }

    /// <summary>
    /// Visualizes: OLD=Extension("56") → NEW=Extension("5")
    /// New extension is WIDER. Old extension's range (56...) is within new range (5...).
    /// Old data survives, will be cleaned by child sync.
    /// </summary>
    [Test]
    public void TypeChange_NarrowExtensionToWiderExtension_OldRangeSurvivesWithinNewRange()
    {
        // NEW node: Extension with key "5" (wider range)
        TrieNode newExtension = TrieNodeFactory.CreateExtension([0x5], TrieNodeFactory.CreateBranch());
        FlatTreeSyncStore.DeletionRange[] ranges = ComputeRanges(TreePath.Empty, newExtension);

        // Extension("5") gaps: 0... to 4fff..., 6... to fff...
        // Old extension("56") had data at 56...
        Assert.Multiple(() =>
        {
            // 56... is within 5..., so NOT deleted
            Assert.That(IsDeleted(ranges, "0x5600000000000000000000000000000000000000000000000000000000000000"),
                Is.False, "Old extension 56... data survives (within new 5... range)");
            Assert.That(IsDeleted(ranges, "0x56ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Is.False, "Old extension 56fff... data survives (within new 5... range)");

            // Other 5X ranges also survive - will be cleaned by child at path "5"
            Assert.That(IsDeleted(ranges, "0x5000000000000000000000000000000000000000000000000000000000000000"),
                Is.False, "50... survives for child to handle");
            Assert.That(IsDeleted(ranges, "0x5affffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Is.False, "5a... survives for child to handle");
        });
    }

    /// <summary>
    /// Visualizes: OLD=Extension("5") → NEW=Extension("56")
    /// New extension is NARROWER. Old extension's range (5...) is partially deleted.
    /// Only 56... survives; 50-55 and 57-5f are deleted by gaps.
    /// </summary>
    [Test]
    public void TypeChange_WiderExtensionToNarrowerExtension_OldRangePartiallyDeleted()
    {
        // NEW node: Extension with key "56" (narrower range)
        TrieNode newExtension = TrieNodeFactory.CreateExtension([0x5, 0x6], TrieNodeFactory.CreateBranch());
        FlatTreeSyncStore.DeletionRange[] ranges = ComputeRanges(TreePath.Empty, newExtension);

        // Extension("56") gaps: 0... to 55fff..., 57... to fff...
        // Old extension("5") had data throughout 5... (50..., 51..., ..., 5f...)
        Assert.Multiple(() =>
        {
            // Ranges outside 56... are deleted
            Assert.That(IsDeleted(ranges, "0x5000000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Old 50... deleted (in before gap)");
            Assert.That(IsDeleted(ranges, "0x5500000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Old 55... deleted (in before gap)");
            Assert.That(IsDeleted(ranges, "0x5700000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Old 57... deleted (in after gap)");
            Assert.That(IsDeleted(ranges, "0x5f00000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Old 5f... deleted (in after gap)");

            // Only 56... survives
            Assert.That(IsDeleted(ranges, "0x5600000000000000000000000000000000000000000000000000000000000000"),
                Is.False, "56... survives (within new extension range)");
            Assert.That(IsDeleted(ranges, "0x56ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Is.False, "56fff... survives (within new extension range)");
        });
    }

    /// <summary>
    /// Visualizes: OLD=Extension("5") → NEW=Branch(child 5 non-null)
    /// Branch's null children delete everything except 5... range.
    /// Old extension's 5... data survives for child sync to handle.
    /// </summary>
    [Test]
    public void TypeChange_ExtensionToBranch_ExtensionRangeSurvivesIfChildNonNull()
    {
        // NEW node: Branch with only child 5 non-null
        TrieNode newBranch = TrieNodeFactory.CreateBranch();
        newBranch[5] = TrieNodeFactory.CreateLeaf([0], DummyValue);
        FlatTreeSyncStore.DeletionRange[] ranges = ComputeRanges(TreePath.Empty, newBranch);

        // Branch null children: 0-4, 6-15
        // Merged ranges: 0... to 4fff..., 6... to fff...
        // Old extension("5") had data at 5...
        Assert.Multiple(() =>
        {
            // 5... survives because child 5 is non-null
            Assert.That(IsDeleted(ranges, "0x5000000000000000000000000000000000000000000000000000000000000000"),
                Is.False, "Old extension 50... survives (child 5 non-null)");
            Assert.That(IsDeleted(ranges, "0x5fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Is.False, "Old extension 5f... survives (child 5 non-null)");

            // Everything else is deleted
            Assert.That(IsDeleted(ranges, "0x0000000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "0... deleted (null child 0)");
            Assert.That(IsDeleted(ranges, "0x4fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                Is.True, "4fff... deleted (null child 4)");
            Assert.That(IsDeleted(ranges, "0x6000000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "6... deleted (null child 6)");
        });
    }

    /// <summary>
    /// Visualizes: OLD=Leaf("5abc...") → NEW=Branch(child 5 non-null)
    /// Old leaf's exact position survives if within non-null child's range.
    /// Cleanup of 5abc... deferred to child sync at path "5".
    /// </summary>
    [Test]
    public void TypeChange_LeafToBranch_LeafPositionSurvivesIfInNonNullChildRange()
    {
        // NEW node: Branch with only child 5 non-null
        TrieNode newBranch = TrieNodeFactory.CreateBranch();
        newBranch[5] = TrieNodeFactory.CreateLeaf([0], DummyValue);
        FlatTreeSyncStore.DeletionRange[] ranges = ComputeRanges(TreePath.Empty, newBranch);

        // Old leaf was at 5abc... (exact position)
        // 5abc... is within 5... (child 5 non-null), so survives
        Assert.That(IsDeleted(ranges, "0x5abc000000000000000000000000000000000000000000000000000000000000"),
            Is.False, "Old leaf at 5abc... survives (within non-null child 5 range)");

        // Child at path "5" will later sync and handle cleanup within 5...
        // If new child is Leaf("def..."), its gaps will delete 5abc...
    }

    /// <summary>
    /// Visualizes the lazy cleanup chain:
    /// 1. Branch at "" with child 5 non-null → 5... survives
    /// 2. Child at "5" is Extension("6") → gaps delete 50-55..., 57-5f...
    /// 3. Child at "56" handles further cleanup within 56...
    /// </summary>
    [Test]
    public void LazyCleanup_DemonstrateTwoLevelCleanup()
    {
        // Level 1: Branch at root, child 5 non-null
        TrieNode rootBranch = TrieNodeFactory.CreateBranch();
        rootBranch[5] = TrieNodeFactory.CreateLeaf([0], DummyValue);
        FlatTreeSyncStore.DeletionRange[] level1Ranges = ComputeRanges(TreePath.Empty, rootBranch);

        // Level 2: Extension("6") at path "5"
        TrieNode childExtension = TrieNodeFactory.CreateExtension([0x6], TrieNodeFactory.CreateBranch());
        FlatTreeSyncStore.DeletionRange[] level2Ranges = ComputeRanges(TreePath.FromHexString("5"), childExtension);

        Assert.Multiple(() =>
        {
            // After level 1: only 5... survives
            Assert.That(IsDeleted(level1Ranges, "0x3000000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Level 1: 3... deleted");
            Assert.That(IsDeleted(level1Ranges, "0x5600000000000000000000000000000000000000000000000000000000000000"),
                Is.False, "Level 1: 56... survives");

            // After level 2: Extension("6") at path "5" cleans up 50-55, 57-5f
            // Gaps are: 50... to 55fff..., 57... to 5ffff...
            Assert.That(IsDeleted(level2Ranges, "0x5000000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Level 2: 50... deleted by extension gap");
            Assert.That(IsDeleted(level2Ranges, "0x5500000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Level 2: 55... deleted by extension gap");
            Assert.That(IsDeleted(level2Ranges, "0x5700000000000000000000000000000000000000000000000000000000000000"),
                Is.True, "Level 2: 57... deleted by extension gap");

            // Only 56... survives for level 3
            Assert.That(IsDeleted(level2Ranges, "0x5600000000000000000000000000000000000000000000000000000000000000"),
                Is.False, "Level 2: 56... survives for child to handle");
        });
    }

    // === Optimized deletion tests (using both existing and new nodes) ===

    private static FlatTreeSyncStore.DeletionRange[] ComputeRangesOptimized(TreePath path, TrieNode newNode, TrieNode existingNode)
    {
        RefList16<FlatTreeSyncStore.DeletionRange> ranges = new();
        FlatTreeSyncStore.ComputeDeletionRanges(path, newNode, existingNode, ref ranges);
        return ranges.AsSpan().ToArray();
    }

    private static IEnumerable<TestCaseData> OptimizedDeletionTestCases()
    {
        static byte[] Nibbles(string hex) =>
            hex.Select(c => (byte)(c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0')).ToArray();

        TrieNode CreateBranchWith(params int[] childIndices)
        {
            TrieNode branch = TrieNodeFactory.CreateBranch();
            foreach (int i in childIndices)
                branch[i] = TrieNodeFactory.CreateLeaf([0], DummyValue);
            return branch;
        }

        // === Branch to Branch: Only deleted removed children ===

        // Existing: children at 0, 1 (null at 2-15); New: children at 0, 3 (null at 1-2, 4-15)
        // Only position 1 needs deletion (went from non-null to null)
        yield return new TestCaseData(
            "", CreateBranchWith(0, 3), CreateBranchWith(0, 1),
            new (string, string)[] { ("0x1000000000000000000000000000000000000000000000000000000000000000", "0x1fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff") },
            new[] { "0x2000000000000000000000000000000000000000000000000000000000000000", "0x4000000000000000000000000000000000000000000000000000000000000000" }
        ).SetDescription("Branch→Branch: Existing(0,1) to New(0,3) deletes only child 1");

        // Same children structure - no deletions
        yield return new TestCaseData(
            "", CreateBranchWith(5), CreateBranchWith(5),
            Array.Empty<(string, string)>(),
            new[] { "0x5000000000000000000000000000000000000000000000000000000000000000" }
        ).SetDescription("Branch→Branch: Same structure yields no deletions");

        // All children to single child - deletes 0-4, 6-15
        yield return new TestCaseData(
            "", CreateBranchWith(5), CreateBranchWith(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15),
            new (string, string)[]
            {
                ("0x0000000000000000000000000000000000000000000000000000000000000000", "0x4fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0x6000000000000000000000000000000000000000000000000000000000000000", "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            },
            new[] { "0x5000000000000000000000000000000000000000000000000000000000000000" }
        ).SetDescription("Branch→Branch: All children to single child deletes 0-4, 6-15");

        // === Same type, same key: No deletion ===

        // Leaf → Leaf with same key
        yield return new TestCaseData(
            "", TrieNodeFactory.CreateLeaf(Nibbles("5000000000000000000000000000000000000000000000000000000000000000"), DummyValue),
            TrieNodeFactory.CreateLeaf(Nibbles("5000000000000000000000000000000000000000000000000000000000000000"), DummyValue),
            Array.Empty<(string, string)>(),
            new[] { "0x5000000000000000000000000000000000000000000000000000000000000000" }
        ).SetDescription("Leaf→Leaf: Same key yields no deletions");

        // Extension → Extension with same key
        yield return new TestCaseData(
            "", TrieNodeFactory.CreateExtension([0x5, 0x6], TrieNodeFactory.CreateBranch()),
            TrieNodeFactory.CreateExtension([0x5, 0x6], TrieNodeFactory.CreateBranch()),
            Array.Empty<(string, string)>(),
            new[] { "0x5600000000000000000000000000000000000000000000000000000000000000" }
        ).SetDescription("Extension→Extension: Same key yields no deletions");

        // === Cross-type transitions ===

        // Leaf at 5abc... → Branch with child 5 non-null: Leaf survives (handled by child sync)
        yield return new TestCaseData(
            "", CreateBranchWith(5),
            TrieNodeFactory.CreateLeaf(Nibbles("5abc000000000000000000000000000000000000000000000000000000000000"), DummyValue),
            Array.Empty<(string, string)>(),
            new[] { "0x5abc000000000000000000000000000000000000000000000000000000000000" }
        ).SetDescription("Leaf→Branch: Leaf at 5abc... survives when branch has child 5");

        // Leaf at 5abc... → Branch with child 6 non-null: Leaf deleted (falls under null child 5)
        yield return new TestCaseData(
            "", CreateBranchWith(6),
            TrieNodeFactory.CreateLeaf(Nibbles("5abc000000000000000000000000000000000000000000000000000000000000"), DummyValue),
            new (string, string)[] { ("0x5abc000000000000000000000000000000000000000000000000000000000000", "0x5abc000000000000000000000000000000000000000000000000000000000000") },
            new[] { "0x6000000000000000000000000000000000000000000000000000000000000000" }
        ).SetDescription("Leaf→Branch: Leaf at 5abc... deleted when branch has null at child 5");

        // Extension("56") → Leaf at 5678...: Deletes extension subtree except leaf path
        yield return new TestCaseData(
            "", TrieNodeFactory.CreateLeaf(Nibbles("5678000000000000000000000000000000000000000000000000000000000000"), DummyValue),
            TrieNodeFactory.CreateExtension([0x5, 0x6], TrieNodeFactory.CreateBranch()),
            new (string, string)[]
            {
                ("0x5600000000000000000000000000000000000000000000000000000000000000", "0x5677ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                ("0x5678000000000000000000000000000000000000000000000000000000000001", "0x56ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
            },
            new[] { "0x5678000000000000000000000000000000000000000000000000000000000000" }
        ).SetDescription("Extension→Leaf: Extension subtree deleted except leaf path");
    }

    [TestCaseSource(nameof(OptimizedDeletionTestCases))]
    public void ComputeDeletionRanges_WithExistingNode_ReturnsOptimizedRanges(
        string hexPath, TrieNode newNode, TrieNode existingNode,
        (string From, string To)[] expectedRanges, string[] survivingPaths)
    {
        TreePath path = hexPath == "" ? TreePath.Empty : TreePath.FromHexString(hexPath);
        FlatTreeSyncStore.DeletionRange[] ranges = ComputeRangesOptimized(path, newNode, existingNode);

        Assert.That(ranges, Has.Length.EqualTo(expectedRanges.Length), $"Expected {expectedRanges.Length} range(s)");

        for (int i = 0; i < expectedRanges.Length; i++)
        {
            Assert.That(ranges[i].From, Is.EqualTo(new ValueHash256(expectedRanges[i].From)), $"Range[{i}].From mismatch");
            Assert.That(ranges[i].To, Is.EqualTo(new ValueHash256(expectedRanges[i].To)), $"Range[{i}].To mismatch");
        }

        foreach (string survivingPath in survivingPaths)
            Assert.That(IsDeleted(ranges, survivingPath), Is.False, $"Path {survivingPath} should NOT be deleted");
    }
}
