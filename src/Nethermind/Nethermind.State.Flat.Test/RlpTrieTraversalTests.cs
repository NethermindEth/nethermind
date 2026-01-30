// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class RlpTrieTraversalTests
{
    // A branch node with 17 empty items (16 children + value)
    // 17 * 0x80 = 17 bytes content
    // RLP prefix: 0xd1 (0xc0 + 17)
    private static readonly byte[] EmptyBranchRlp = Bytes.FromHexString("0xd18080808080808080808080808080808080");

    // A branch node with a hash child at index 0 and rest empty
    // Item 0: 0xa0 + 32 bytes of 0xff = 33 bytes
    // Items 1-16: 16 * 0x80 = 16 bytes
    // Total: 33 + 16 = 49 bytes
    // RLP prefix: 0xf1 (0xc0 + 49)
    private static readonly byte[] BranchWithHashAtIndex0 = Bytes.FromHexString(
        "0xf1a0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff80808080808080808080808080808080");

    // A branch node with a hash child at index 15 and rest empty
    // Items 0-14: 15 * 0x80 = 15 bytes
    // Item 15: 0xa0 + 32 bytes of 0xaa = 33 bytes
    // Item 16 (value): 0x80 = 1 byte
    // Total: 15 + 33 + 1 = 49 bytes
    // RLP prefix: 0xf1 (0xc0 + 49)
    private static readonly byte[] BranchWithHashAtIndex15 = Bytes.FromHexString(
        "0xf1808080808080808080808080808080a0aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa80");

    // An extension node with even-length key [a, b, c, d] and hash child
    // HP encoding: 0x00 prefix for even extension, then key bytes: 0x00abcd (3 bytes)
    // Key RLP: 0x83 0x00 0xab 0xcd (4 bytes)
    // Child RLP: 0xa0 + 32 bytes of 0xff = 33 bytes
    // Total content: 4 + 33 = 37 bytes
    // RLP prefix: 0xe5 (0xc0 + 37)
    private static readonly byte[] ExtensionWithEvenKey = Bytes.FromHexString(
        "0xe58300abcda0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    // An extension node with odd-length key [a, b, c] and hash child
    // HP encoding: 0x1 prefix for odd extension, then key: 0x1abc (2 bytes)
    // Key RLP: 0x82 0x1a 0xbc (3 bytes)
    // Child RLP: 0xa0 + 32 bytes of 0xbb = 33 bytes
    // Total content: 3 + 33 = 36 bytes
    // RLP prefix: 0xe4 (0xc0 + 36)
    private static readonly byte[] ExtensionWithOddKey = Bytes.FromHexString(
        "0xe4821abca0bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    // A leaf node with even-length key [a, b, c, d] and small value
    // HP encoding: 0x20 prefix for even leaf, then key bytes: 0x20abcd (3 bytes)
    // Key RLP: 0x83 0x20 0xab 0xcd (4 bytes)
    // Value RLP: 0x84 0xde 0xad 0xbe 0xef (5 bytes)
    // Total content: 4 + 5 = 9 bytes
    // RLP prefix: 0xc9 (0xc0 + 9)
    private static readonly byte[] LeafWithEvenKey = Bytes.FromHexString("0xc98320abcd84deadbeef");

    // A leaf node with odd-length key [a, b, c] and small value
    // HP encoding: 0x3 prefix for odd leaf, then key: 0x3abc (2 bytes)
    // Key RLP: 0x82 0x3a 0xbc (3 bytes)
    // Value RLP: 0x84 0x12 0x34 0x56 0x78 (5 bytes)
    // Total content: 3 + 5 = 8 bytes
    // RLP prefix: 0xc8 (0xc0 + 8)
    private static readonly byte[] LeafWithOddKey = Bytes.FromHexString("0xc8823abc8412345678");

    #region GetNodeType Tests

    [Test]
    public void GetNodeType_EmptyRlp_ReturnsInvalid() =>
        RlpTrieTraversal.GetNodeType(ReadOnlySpan<byte>.Empty).Should().Be(RlpTrieTraversal.RlpNodeType.Invalid);

    [Test]
    public void GetNodeType_InvalidRlp_ReturnsInvalid()
    {
        // Not a valid RLP sequence
        byte[] invalidRlp = Bytes.FromHexString("0x00112233");
        RlpTrieTraversal.GetNodeType(invalidRlp).Should().Be(RlpTrieTraversal.RlpNodeType.Invalid);
    }

    [Test]
    public void GetNodeType_EmptyBranchNode_ReturnsBranch() =>
        RlpTrieTraversal.GetNodeType(EmptyBranchRlp).Should().Be(RlpTrieTraversal.RlpNodeType.Branch);

    [Test]
    public void GetNodeType_BranchWithHashChild_ReturnsBranch() =>
        RlpTrieTraversal.GetNodeType(BranchWithHashAtIndex0).Should().Be(RlpTrieTraversal.RlpNodeType.Branch);

    [Test]
    public void GetNodeType_ExtensionWithEvenKey_ReturnsExtension() =>
        RlpTrieTraversal.GetNodeType(ExtensionWithEvenKey).Should().Be(RlpTrieTraversal.RlpNodeType.Extension);

    [Test]
    public void GetNodeType_ExtensionWithOddKey_ReturnsExtension() =>
        RlpTrieTraversal.GetNodeType(ExtensionWithOddKey).Should().Be(RlpTrieTraversal.RlpNodeType.Extension);

    [Test]
    public void GetNodeType_LeafWithEvenKey_ReturnsLeaf() =>
        RlpTrieTraversal.GetNodeType(LeafWithEvenKey).Should().Be(RlpTrieTraversal.RlpNodeType.Leaf);

    [Test]
    public void GetNodeType_LeafWithOddKey_ReturnsLeaf() =>
        RlpTrieTraversal.GetNodeType(LeafWithOddKey).Should().Be(RlpTrieTraversal.RlpNodeType.Leaf);

    #endregion

    #region GetKey Tests

    [Test]
    public void GetKey_EmptyRlp_ReturnsNull() =>
        RlpTrieTraversal.GetKey(ReadOnlySpan<byte>.Empty).Should().BeNull();

    [Test]
    public void GetKey_BranchNode_ReturnsNull() =>
        RlpTrieTraversal.GetKey(EmptyBranchRlp).Should().BeNull();

    [Test]
    public void GetKey_LeafWithEvenKey_ReturnsCorrectNibbles()
    {
        byte[]? key = RlpTrieTraversal.GetKey(LeafWithEvenKey);
        key.Should().NotBeNull();
        // HP 0x20abcd decodes to nibbles [a, b, c, d]
        key.Should().BeEquivalentTo(new byte[] { 0x0a, 0x0b, 0x0c, 0x0d });
    }

    [Test]
    public void GetKey_LeafWithOddKey_ReturnsCorrectNibbles()
    {
        byte[]? key = RlpTrieTraversal.GetKey(LeafWithOddKey);
        key.Should().NotBeNull();
        // HP 0x3abc decodes to nibbles [a, b, c]
        key.Should().BeEquivalentTo(new byte[] { 0x0a, 0x0b, 0x0c });
    }

    [Test]
    public void GetKey_ExtensionWithEvenKey_ReturnsCorrectNibbles()
    {
        byte[]? key = RlpTrieTraversal.GetKey(ExtensionWithEvenKey);
        key.Should().NotBeNull();
        // HP 0x00abcd decodes to nibbles [a, b, c, d]
        key.Should().BeEquivalentTo(new byte[] { 0x0a, 0x0b, 0x0c, 0x0d });
    }

    [Test]
    public void GetKey_ExtensionWithOddKey_ReturnsCorrectNibbles()
    {
        byte[]? key = RlpTrieTraversal.GetKey(ExtensionWithOddKey);
        key.Should().NotBeNull();
        // HP 0x1abc decodes to nibbles [a, b, c]
        key.Should().BeEquivalentTo(new byte[] { 0x0a, 0x0b, 0x0c });
    }

    #endregion

    #region TryGetBranchChildHash Tests

    [Test]
    public void TryGetBranchChildHash_EmptyRlp_ReturnsFalse() =>
        RlpTrieTraversal.TryGetBranchChildHash(ReadOnlySpan<byte>.Empty, 0, out _).Should().BeFalse();

    [TestCase(-1)]
    [TestCase(16)]
    [TestCase(17)]
    public void TryGetBranchChildHash_InvalidIndex_ReturnsFalse(int index) =>
        RlpTrieTraversal.TryGetBranchChildHash(BranchWithHashAtIndex0, index, out _).Should().BeFalse();

    [Test]
    public void TryGetBranchChildHash_EmptyChild_ReturnsFalse()
    {
        // All children in EmptyBranchRlp are 0x80 (empty)
        for (int i = 0; i < 16; i++)
        {
            RlpTrieTraversal.TryGetBranchChildHash(EmptyBranchRlp, i, out Hash256? hash).Should().BeFalse();
            hash.Should().BeNull();
        }
    }

    [Test]
    public void TryGetBranchChildHash_HashChildAtIndex0_ReturnsTrue()
    {
        bool result = RlpTrieTraversal.TryGetBranchChildHash(BranchWithHashAtIndex0, 0, out Hash256? hash);
        result.Should().BeTrue();
        hash.Should().NotBeNull();
        hash!.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0xff);
    }

    [Test]
    public void TryGetBranchChildHash_EmptyChildrenInBranchWithHash_ReturnsFalse()
    {
        // Children 1-15 should be empty in BranchWithHashAtIndex0
        for (int i = 1; i < 16; i++)
        {
            RlpTrieTraversal.TryGetBranchChildHash(BranchWithHashAtIndex0, i, out Hash256? hash).Should().BeFalse();
            hash.Should().BeNull();
        }
    }

    [Test]
    public void TryGetBranchChildHash_HashChildAtIndex15_ReturnsTrue()
    {
        bool result = RlpTrieTraversal.TryGetBranchChildHash(BranchWithHashAtIndex15, 15, out Hash256? hash);
        result.Should().BeTrue();
        hash.Should().NotBeNull();
        hash!.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0xaa);
    }

    [Test]
    public void TryGetBranchChildValueHash_HashChild_ReturnsTrue()
    {
        bool result = RlpTrieTraversal.TryGetBranchChildValueHash(BranchWithHashAtIndex0, 0, out ValueHash256 hash);
        result.Should().BeTrue();
        hash.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0xff);
    }

    #endregion

    #region TryGetExtensionChildHash Tests

    [Test]
    public void TryGetExtensionChildHash_EmptyRlp_ReturnsFalse() =>
        RlpTrieTraversal.TryGetExtensionChildHash(ReadOnlySpan<byte>.Empty, out _).Should().BeFalse();

    [Test]
    public void TryGetExtensionChildHash_LeafNode_ReturnsFalse()
    {
        RlpTrieTraversal.TryGetExtensionChildHash(LeafWithEvenKey, out Hash256? hash).Should().BeFalse();
        hash.Should().BeNull();
    }

    [Test]
    public void TryGetExtensionChildHash_BranchNode_ReturnsFalse()
    {
        RlpTrieTraversal.TryGetExtensionChildHash(EmptyBranchRlp, out Hash256? hash).Should().BeFalse();
        hash.Should().BeNull();
    }

    [Test]
    public void TryGetExtensionChildHash_ExtensionWithEvenKey_ReturnsTrue()
    {
        bool result = RlpTrieTraversal.TryGetExtensionChildHash(ExtensionWithEvenKey, out Hash256? hash);
        result.Should().BeTrue();
        hash.Should().NotBeNull();
        hash!.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0xff);
    }

    [Test]
    public void TryGetExtensionChildHash_ExtensionWithOddKey_ReturnsTrue()
    {
        bool result = RlpTrieTraversal.TryGetExtensionChildHash(ExtensionWithOddKey, out Hash256? hash);
        result.Should().BeTrue();
        hash.Should().NotBeNull();
        hash!.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0xbb);
    }

    [Test]
    public void TryGetExtensionChildValueHash_ExtensionWithHash_ReturnsTrue()
    {
        bool result = RlpTrieTraversal.TryGetExtensionChildValueHash(ExtensionWithEvenKey, out ValueHash256 hash);
        result.Should().BeTrue();
        hash.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0xff);
    }

    #endregion

    #region IsBranchChildEmpty Tests

    [Test]
    public void IsBranchChildEmpty_EmptyRlp_ReturnsTrue() =>
        RlpTrieTraversal.IsBranchChildEmpty(ReadOnlySpan<byte>.Empty, 0).Should().BeTrue();

    [Test]
    public void IsBranchChildEmpty_AllChildrenInEmptyBranch_ReturnsTrue()
    {
        for (int i = 0; i < 16; i++)
        {
            RlpTrieTraversal.IsBranchChildEmpty(EmptyBranchRlp, i).Should().BeTrue();
        }
    }

    [Test]
    public void IsBranchChildEmpty_HashChild_ReturnsFalse() =>
        RlpTrieTraversal.IsBranchChildEmpty(BranchWithHashAtIndex0, 0).Should().BeFalse();

    [Test]
    public void IsBranchChildEmpty_EmptyChildInBranchWithHash_ReturnsTrue() =>
        RlpTrieTraversal.IsBranchChildEmpty(BranchWithHashAtIndex0, 1).Should().BeTrue();

    #endregion

    #region IsBranchChildInline Tests

    [Test]
    public void IsBranchChildInline_EmptyRlp_ReturnsFalse() =>
        RlpTrieTraversal.IsBranchChildInline(ReadOnlySpan<byte>.Empty, 0).Should().BeFalse();

    [Test]
    public void IsBranchChildInline_EmptyChild_ReturnsFalse() =>
        RlpTrieTraversal.IsBranchChildInline(EmptyBranchRlp, 0).Should().BeFalse();

    [Test]
    public void IsBranchChildInline_HashChild_ReturnsFalse() =>
        RlpTrieTraversal.IsBranchChildInline(BranchWithHashAtIndex0, 0).Should().BeFalse();

    [Test]
    public void IsBranchChildInline_InlineChild_ReturnsTrue()
    {
        // Create a branch with an inline leaf node at index 0
        // Inline leaf: c0 (empty list) - simplest valid inline
        // This creates a branch where index 0 has an inline node (RLP list)
        // Items: inline (c0), 15 empty (80), value (80)
        // Total: 1 + 15 + 1 = 17 bytes
        // Prefix: 0xd1
        byte[] branchWithInline = Bytes.FromHexString("0xd1c080808080808080808080808080808080");

        RlpTrieTraversal.IsBranchChildInline(branchWithInline, 0).Should().BeTrue();
    }

    #endregion

    #region Integration Tests

    [Test]
    public void Integration_RoundTrip_BranchWithMultipleHashes()
    {
        // Create a branch with hashes at multiple positions
        // Index 0: hash (0xff)
        // Index 5: hash (0xaa)
        // Index 10: hash (0x55)
        // Rest: empty

        // Build the branch manually
        byte[] hash0 = new byte[32];
        byte[] hash5 = new byte[32];
        byte[] hash10 = new byte[32];
        Array.Fill(hash0, (byte)0xff);
        Array.Fill(hash5, (byte)0xaa);
        Array.Fill(hash10, (byte)0x55);

        // Encode manually using RlpStream
        int contentLength = 33 + 33 + 33 + 14; // 3 hashes (33 each) + 14 empty items
        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        stream.StartSequence(contentLength);

        for (int i = 0; i < 17; i++)
        {
            switch (i)
            {
                case 0:
                    stream.Encode(hash0);
                    break;
                case 5:
                    stream.Encode(hash5);
                    break;
                case 10:
                    stream.Encode(hash10);
                    break;
                default:
                    stream.Encode(Bytes.Empty);
                    break;
            }
        }

        byte[] branchRlp = stream.Data.AsSpan().ToArray();

        // Verify node type
        RlpTrieTraversal.GetNodeType(branchRlp).Should().Be(RlpTrieTraversal.RlpNodeType.Branch);

        // Verify we can retrieve each hash
        RlpTrieTraversal.TryGetBranchChildHash(branchRlp, 0, out Hash256? h0).Should().BeTrue();
        h0!.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0xff);

        RlpTrieTraversal.TryGetBranchChildHash(branchRlp, 5, out Hash256? h5).Should().BeTrue();
        h5!.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0xaa);

        RlpTrieTraversal.TryGetBranchChildHash(branchRlp, 10, out Hash256? h10).Should().BeTrue();
        h10!.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0x55);

        // Verify empty positions
        RlpTrieTraversal.TryGetBranchChildHash(branchRlp, 1, out _).Should().BeFalse();
        RlpTrieTraversal.IsBranchChildEmpty(branchRlp, 1).Should().BeTrue();
    }

    [Test]
    public void Integration_AllNodeTypes_CorrectlyIdentified()
    {
        // Test that all node types are correctly identified
        var testCases = new (byte[] rlp, RlpTrieTraversal.RlpNodeType expected)[]
        {
            (EmptyBranchRlp, RlpTrieTraversal.RlpNodeType.Branch),
            (BranchWithHashAtIndex0, RlpTrieTraversal.RlpNodeType.Branch),
            (ExtensionWithEvenKey, RlpTrieTraversal.RlpNodeType.Extension),
            (ExtensionWithOddKey, RlpTrieTraversal.RlpNodeType.Extension),
            (LeafWithEvenKey, RlpTrieTraversal.RlpNodeType.Leaf),
            (LeafWithOddKey, RlpTrieTraversal.RlpNodeType.Leaf),
        };

        foreach (var (rlp, expected) in testCases)
        {
            RlpTrieTraversal.GetNodeType(rlp).Should().Be(expected);
        }
    }

    #endregion
}
