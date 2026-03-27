// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class RefCountingTrieNodeTests
{
    private RefCountingRlpNodePoolTracker _tracker = null!;

    [SetUp]
    public void SetUp() =>
        _tracker = new RefCountingRlpNodePoolTracker(new RefCountingTrieNodePool());

    /// <summary>
    /// Builds a minimal branch node RLP with 16 hash children + empty value.
    /// Layout: [3B seq prefix][16 x 33B (0xA0 + 32B hash)][1B empty value (0x80)] = 532B
    /// </summary>
    private static byte[] BuildBranchRlp()
    {
        // Full branch: sequence of 16 hash refs + 1 empty value = 529 content bytes
        // RLP sequence: prefix 0xF9 + 2-byte length + content
        int contentLen = 16 * 33 + 1; // 529
        byte[] rlp = new byte[3 + contentLen]; // 532
        rlp[0] = 0xF9;
        rlp[1] = (byte)(contentLen >> 8);
        rlp[2] = (byte)(contentLen & 0xFF);
        int pos = 3;
        for (int i = 0; i < 16; i++)
        {
            rlp[pos++] = 0xA0; // string prefix for 32 bytes
            for (int j = 0; j < 32; j++)
            {
                rlp[pos++] = (byte)(i + j); // deterministic hash bytes
            }
        }
        rlp[pos] = 0x80; // empty value
        return rlp;
    }

    /// <summary>
    /// Builds a leaf node RLP: sequence of [compact key, value].
    /// Compact key: 0x20 (even leaf, no nibbles) for simplicity.
    /// </summary>
    private static byte[] BuildLeafRlp(byte[] value)
    {
        // Key: single byte 0x20 (leaf, even, empty path)
        // Value: RLP-encoded byte array
        int valRlpLen = Rlp.LengthOf(value);
        int seqContentLen = 1 + valRlpLen; // key is single byte < 0x80 so 1 byte
        byte[] rlp = new byte[Rlp.LengthOfSequence(seqContentLen)];
        RlpStream stream = new RlpStream(rlp);
        stream.StartSequence(seqContentLen);
        stream.WriteByte(0x20); // compact leaf prefix
        stream.Encode(value);
        return rlp;
    }

    [Test]
    public void Rent_And_Dispose_Returns_To_Pool()
    {
        byte[] branchRlp = BuildBranchRlp();
        ValueHash256 hash = ValueKeccak.Compute(branchRlp);

        RefCountingTrieNode node = _tracker.Rent(hash, branchRlp);
        Assert.That(_tracker.ActiveCount, Is.EqualTo(1));

        node.Dispose();
        Assert.That(_tracker.ActiveCount, Is.EqualTo(0));
    }

    [Test]
    public void Multiple_Leases_Keeps_Node_Alive()
    {
        byte[] branchRlp = BuildBranchRlp();
        ValueHash256 hash = ValueKeccak.Compute(branchRlp);

        RefCountingTrieNode node = _tracker.Rent(hash, branchRlp);
        node.AcquireLease(); // 2 leases
        node.AcquireLease(); // 3 leases

        Assert.That(_tracker.ActiveCount, Is.EqualTo(1));

        node.Dispose(); // 2 leases
        Assert.That(_tracker.ActiveCount, Is.EqualTo(1));

        node.Dispose(); // 1 lease
        Assert.That(_tracker.ActiveCount, Is.EqualTo(1));

        node.Dispose(); // 0 -> cleanup -> return to pool
        Assert.That(_tracker.ActiveCount, Is.EqualTo(0));
    }

    [Test]
    public void Initialize_Sets_Hash_And_Rlp()
    {
        byte[] branchRlp = BuildBranchRlp();
        ValueHash256 hash = ValueKeccak.Compute(branchRlp);

        RefCountingTrieNode node = _tracker.Rent(hash, branchRlp);
        Assert.That(node.Hash, Is.EqualTo(hash));
        Assert.That(node.RlpSpan.ToArray(), Is.EqualTo(branchRlp));

        node.Dispose();
    }

    [Test]
    public void ParseMetadata_Branch_Correct_NodeType_And_ChildOffsets()
    {
        byte[] branchRlp = BuildBranchRlp();
        ValueHash256 hash = ValueKeccak.Compute(branchRlp);

        RefCountingTrieNode node = _tracker.Rent(hash, branchRlp);

        Assert.That(node.NodeType, Is.EqualTo(NodeType.Branch));

        // All 16 children should have non-zero offsets (they're all hash refs)
        for (int i = 0; i < 16; i++)
        {
            short offset = node.ChildOffsets[i];
            Assert.That(offset, Is.GreaterThan((short)0), $"Child {i} offset should be non-zero");

            // Verify the offset points to 0xA0 (hash ref prefix)
            Assert.That(branchRlp[offset], Is.EqualTo((byte)0xA0), $"Child {i} should point to hash ref prefix");
        }

        // ChildOffsetBuffer is now 16 entries (no value slot)

        node.Dispose();
    }

    [Test]
    public void ParseMetadata_Leaf_Correct_NodeType()
    {
        byte[] leafRlp = BuildLeafRlp([0x42]);
        ValueHash256 hash = ValueKeccak.Compute(leafRlp);

        RefCountingTrieNode node = _tracker.Rent(hash, leafRlp);

        Assert.That(node.NodeType, Is.EqualTo(NodeType.Leaf));

        node.Dispose();
    }

    [Test]
    public void GetChildHash_Returns_Correct_Hash_For_Branch()
    {
        byte[] branchRlp = BuildBranchRlp();
        ValueHash256 hash = ValueKeccak.Compute(branchRlp);

        RefCountingTrieNode node = _tracker.Rent(hash, branchRlp);

        for (int i = 0; i < 16; i++)
        {
            Hash256? childHash = node.GetChildHash(i);
            Assert.That(childHash, Is.Not.Null, $"Child {i} hash should not be null");

            // Verify the hash bytes match what we put in
            byte[] expectedHash = new byte[32];
            for (int j = 0; j < 32; j++) expectedHash[j] = (byte)(i + j);
            Assert.That(childHash!.Bytes.ToArray(), Is.EqualTo(expectedHash), $"Child {i} hash mismatch");
        }

        node.Dispose();
    }

    [Test]
    public void Pool_Reuses_Returned_Nodes()
    {
        byte[] branchRlp = BuildBranchRlp();
        ValueHash256 hash = ValueKeccak.Compute(branchRlp);

        RefCountingTrieNode node1 = _tracker.Rent(hash, branchRlp);
        node1.Dispose(); // returns to pool

        RefCountingTrieNode node2 = _tracker.Rent(hash, branchRlp);
        // Should be the same instance reused from the pool
        Assert.That(ReferenceEquals(node1, node2), Is.True);

        node2.Dispose();
    }

    /// <summary>
    /// Builds a sparse branch with only <paramref name="hashRefCount"/> hash ref children,
    /// rest are empty (0x80). Children at indices 0..hashRefCount-1 are hash refs.
    /// </summary>
    private static byte[] BuildSparseBranchRlp(int hashRefCount)
    {
        int contentLen = hashRefCount * 33 + (16 - hashRefCount) + 1; // hash refs + empties + value
        int prefixLen = contentLen < 56 ? 1 : contentLen < 256 ? 2 : 3;
        byte[] rlp = new byte[prefixLen + contentLen];
        int pos = 0;
        if (prefixLen == 1)
        {
            rlp[pos++] = (byte)(0xC0 + contentLen);
        }
        else if (prefixLen == 2)
        {
            rlp[pos++] = 0xF8;
            rlp[pos++] = (byte)contentLen;
        }
        else
        {
            rlp[pos++] = 0xF9;
            rlp[pos++] = (byte)(contentLen >> 8);
            rlp[pos++] = (byte)(contentLen & 0xFF);
        }
        for (int i = 0; i < 16; i++)
        {
            if (i < hashRefCount)
            {
                rlp[pos++] = 0xA0;
                for (int j = 0; j < 32; j++) rlp[pos++] = (byte)(i + j);
            }
            else
            {
                rlp[pos++] = 0x80;
            }
        }
        rlp[pos] = 0x80; // empty value
        return rlp;
    }

    [Test]
    [TestCase(0, Description = "All empties")]
    [TestCase(2, Description = "2 hash refs + 14 empties")]
    [TestCase(4, Description = "4 hash refs + 12 empties")]
    [TestCase(8, Description = "8 hash refs + 8 empties")]
    [TestCase(14, Description = "14 hash refs + 2 empties")]
    public void SparseBranch_CorrectOffsets(int hashRefCount)
    {
        byte[] rlp = BuildSparseBranchRlp(hashRefCount);
        ValueHash256 hash = ValueKeccak.Compute(rlp);

        RefCountingTrieNode node = _tracker.Rent(hash, rlp);
        Assert.That(node.NodeType, Is.EqualTo(NodeType.Branch));

        for (int i = 0; i < 16; i++)
        {
            if (i < hashRefCount)
            {
                Assert.That(node.ChildOffsets[i], Is.GreaterThan((short)0), $"Child {i} should have offset");
                Assert.That(rlp[node.ChildOffsets[i]], Is.EqualTo((byte)0xA0), $"Child {i} should point to hash ref");
            }
            else
            {
                Assert.That(node.ChildOffsets[i], Is.EqualTo((short)0), $"Child {i} should be empty");
            }
        }

        node.Dispose();
    }

}
