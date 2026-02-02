// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class FlatEntryWriterTests
{
    private static readonly byte[] SmallSlotValue = [0x01];

    // 56-nibble key creates leaf RLP > 32 bytes (becomes hash reference)
    private const string LargeKeyHex = "1234567890abcdef1234567890abcdef1234567890abcdef12345678";

    private static byte[] Nibbles(string hex) =>
        hex.Select(c => (byte)(c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0')).ToArray();

    private static byte[] SmallAccountRlp() => new AccountDecoder().Encode(new Account(0, 1)).Bytes;

    #region WriteAccountFlatEntries Tests

    [TestCase(
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890ab", // 60 nibble path
        "cdef",  // 4 nibble leaf key
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef")] // 64 nibbles
    [TestCase(
        "abcdef1234567890abcdef1234567890abcdef1234567890abcdef123456", // 60 nibble path
        "7890",  // 4 nibble leaf key
        "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890")] // 64 nibbles
    public void WriteAccountFlatEntries_LeafNode_WritesAccountAtCorrectPath(
        string pathHex, string leafKeyHex, string expectedPathHex)
    {
        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        TreePath path = TreePath.FromHexString(pathHex);
        Hash256 expectedPath = new(Bytes.FromHexString(expectedPathHex));
        TrieNode leaf = TrieNodeFactory.CreateLeaf(Nibbles(leafKeyHex), SmallAccountRlp());
        TreePath empty = TreePath.Empty;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref empty);

        FlatEntryWriter.WriteAccountFlatEntries(writeBatch, ref path, leaf);

        writeBatch.Received(1).SetAccountRaw(expectedPath, Arg.Any<Account>());
    }

    [Test]
    public void WriteAccountFlatEntries_BranchAndExtension_WithHashReferences_WritesNothing()
    {
        // Account RLP is ~70 bytes, always becomes hash reference in branch/extension
        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        TreePath path = TreePath.FromHexString("1234");
        TreePath empty = TreePath.Empty;

        // Branch with account leaves (too large to inline)
        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch[3] = TrieNodeFactory.CreateLeaf([0xa], SmallAccountRlp());
        branch.ResolveKey(NullTrieNodeResolver.Instance, ref empty);
        FlatEntryWriter.WriteAccountFlatEntries(writeBatch, ref path, branch);

        // Extension with account leaf (too large to inline)
        TrieNode extension = TrieNodeFactory.CreateExtension([0x5], TrieNodeFactory.CreateLeaf([0xb], SmallAccountRlp()));
        empty = TreePath.Empty;
        extension.ResolveKey(NullTrieNodeResolver.Instance, ref empty);
        FlatEntryWriter.WriteAccountFlatEntries(writeBatch, ref path, extension);

        writeBatch.DidNotReceive().SetAccountRaw(Arg.Any<Hash256>(), Arg.Any<Account>());
    }

    #endregion

    #region WriteStorageFlatEntries Tests

    [TestCase(
        "abcdef1234567890abcdef1234567890abcdef1234567890abcdef123456", // 60 nibble path
        "7890",  // 4 nibble leaf key
        "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890")] // 64 nibbles
    [TestCase(
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890ab", // 60 nibble path
        "cdef",  // 4 nibble leaf key
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef")] // 64 nibbles
    public void WriteStorageFlatEntries_LeafNode_WritesStorageAtCorrectPath(
        string pathHex, string leafKeyHex, string expectedPathHex)
    {
        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        Hash256 address = Keccak.Compute("address");
        TreePath path = TreePath.FromHexString(pathHex);
        Hash256 expectedPath = new(Bytes.FromHexString(expectedPathHex));
        TrieNode leaf = TrieNodeFactory.CreateLeaf(Nibbles(leafKeyHex), SmallSlotValue);
        TreePath empty = TreePath.Empty;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref empty);

        FlatEntryWriter.WriteStorageFlatEntries(writeBatch, address, path, leaf);

        writeBatch.Received(1).SetStorageRaw(address, expectedPath, Arg.Any<SlotValue?>());
    }

    // path (62 nibbles) + branch index (1) + leaf key (1) = 64 nibbles
    [TestCase(3, 7, "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcd", "a", "b",
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcd3a",
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcd7b")]
    [TestCase(0, 15, "abcdef1234567890abcdef1234567890abcdef1234567890abcdef12345678", "c", "d",
        "abcdef1234567890abcdef1234567890abcdef1234567890abcdef123456780c",
        "abcdef1234567890abcdef1234567890abcdef1234567890abcdef12345678fd")]
    public void WriteStorageFlatEntries_BranchWithInlineLeaves_WritesAllInlineLeavesAtCorrectPaths(
        int index1, int index2, string pathHex, string leafKey1Hex, string leafKey2Hex, string expectedPath1Hex, string expectedPath2Hex)
    {
        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        Hash256 address = Keccak.Compute("address");
        TreePath path = TreePath.FromHexString(pathHex);
        Hash256 expectedPath1 = new(Bytes.FromHexString(expectedPath1Hex));
        Hash256 expectedPath2 = new(Bytes.FromHexString(expectedPath2Hex));

        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch[index1] = TrieNodeFactory.CreateLeaf(Nibbles(leafKey1Hex), SmallSlotValue);
        branch[index2] = TrieNodeFactory.CreateLeaf(Nibbles(leafKey2Hex), SmallSlotValue);
        TreePath empty = TreePath.Empty;
        branch.ResolveKey(NullTrieNodeResolver.Instance, ref empty);

        FlatEntryWriter.WriteStorageFlatEntries(writeBatch, address, path, branch);

        writeBatch.Received(1).SetStorageRaw(address, expectedPath1, Arg.Any<SlotValue?>());
        writeBatch.Received(1).SetStorageRaw(address, expectedPath2, Arg.Any<SlotValue?>());
    }

    [Test]
    public void WriteStorageFlatEntries_BranchWithMixedChildren_OnlyWritesInlineLeaves()
    {
        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        Hash256 address = Keccak.Compute("address");
        TreePath path = TreePath.FromHexString("abcd1234");
        Hash256 expectedPath = new(Bytes.FromHexString("abcd123435000000000000000000000000000000000000000000000000000000"));

        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch[3] = TrieNodeFactory.CreateLeaf([5], SmallSlotValue);  // Inline (small)
        branch[7] = TrieNodeFactory.CreateLeaf(Nibbles(LargeKeyHex), SmallSlotValue);  // Hash ref (large)
        TreePath empty = TreePath.Empty;
        branch.ResolveKey(NullTrieNodeResolver.Instance, ref empty);

        FlatEntryWriter.WriteStorageFlatEntries(writeBatch, address, path, branch);

        writeBatch.Received(1).SetStorageRaw(address, expectedPath, Arg.Any<SlotValue?>());
    }

    // path (61 nibbles) + extension key (2 nibbles) + leaf key (1 nibble) = 64 nibbles
    [TestCase("56", "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abc", "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abc560")]
    [TestCase("ab", "fedcba0987654321fedcba0987654321fedcba0987654321fedcba098765a", "fedcba0987654321fedcba0987654321fedcba0987654321fedcba098765aab0")]
    public void WriteStorageFlatEntries_ExtensionWithInlineLeaf_WritesInlineLeafAtCorrectPath(
        string extensionKeyHex, string pathHex, string expectedPathHex)
    {
        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        Hash256 address = Keccak.Compute("address");
        TreePath path = TreePath.FromHexString(pathHex);
        Hash256 expectedPath = new(Bytes.FromHexString(expectedPathHex));

        TrieNode extension = TrieNodeFactory.CreateExtension(Nibbles(extensionKeyHex), TrieNodeFactory.CreateLeaf([0], SmallSlotValue));
        TreePath empty = TreePath.Empty;
        extension.ResolveKey(NullTrieNodeResolver.Instance, ref empty);

        FlatEntryWriter.WriteStorageFlatEntries(writeBatch, address, path, extension);

        writeBatch.Received(1).SetStorageRaw(address, expectedPath, Arg.Any<SlotValue?>());
    }

    #endregion

    #region BranchInlineChildLeafEnumerator Tests

    [TestCase("abcd", new[] { 5, 10 }, 2)]
    [TestCase("1234", new[] { 0, 8, 15 }, 3)]
    [TestCase("5678", new[] { 3 }, 1)]
    public void BranchEnumerator_WithInlineLeaves_YieldsCorrectCountAndReturnsFullPath(
        string pathHex, int[] indices, int expectedCount)
    {
        TrieNode branch = TrieNodeFactory.CreateBranch();
        foreach (int idx in indices)
            branch[idx] = TrieNodeFactory.CreateLeaf([0xa], SmallSlotValue);
        TreePath empty = TreePath.Empty;
        branch.ResolveKey(NullTrieNodeResolver.Instance, ref empty);

        TreePath path = TreePath.FromHexString(pathHex);
        FlatEntryWriter.BranchInlineChildLeafEnumerator enumerator = new(ref path, branch);

        int count = 0;
        while (enumerator.MoveNext())
        {
            count++;
            // CurrentPath is now ValueHash256 (complete 64-nibble path)
            Assert.That(enumerator.CurrentPath, Is.Not.EqualTo(default(ValueHash256)));
        }

        Assert.That(count, Is.EqualTo(expectedCount));
    }

    [Test]
    public void BranchEnumerator_SkipsHashReferences_AndCurrentNodeReturnsValidLeaf()
    {
        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch[3] = TrieNodeFactory.CreateLeaf([0xb], SmallSlotValue);  // Inline
        branch[7] = TrieNodeFactory.CreateLeaf(Nibbles(LargeKeyHex), SmallSlotValue);  // Hash ref
        TreePath empty = TreePath.Empty;
        branch.ResolveKey(NullTrieNodeResolver.Instance, ref empty);

        TreePath path = TreePath.FromHexString("5678");
        FlatEntryWriter.BranchInlineChildLeafEnumerator enumerator = new(ref path, branch);

        int count = 0;
        while (enumerator.MoveNext())
        {
            count++;
            Assert.That(enumerator.CurrentNode.IsLeaf, Is.True);
        }

        Assert.That(count, Is.EqualTo(1));
    }

    #endregion

    #region ExtensionInlineChildLeafEnumerator Tests

    [Test]
    public void ExtensionEnumerator_WithInlineLeaf_YieldsOnceWithFullPath()
    {
        TrieNode extension = TrieNodeFactory.CreateExtension(Nibbles("56"), TrieNodeFactory.CreateLeaf([0xc], SmallSlotValue));
        TreePath empty = TreePath.Empty;
        extension.ResolveKey(NullTrieNodeResolver.Instance, ref empty);

        TreePath path = TreePath.FromHexString("1234");
        FlatEntryWriter.ExtensionInlineChildLeafEnumerator enumerator = new(ref path, extension);

        int count = 0;
        while (enumerator.MoveNext())
        {
            count++;
            // CurrentPath is now ValueHash256 (complete 64-nibble path)
            Assert.That(enumerator.CurrentPath, Is.Not.EqualTo(default(ValueHash256)));
            Assert.That(enumerator.CurrentNode.IsLeaf, Is.True);
        }

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void ExtensionEnumerator_WithHashReference_YieldsNothing()
    {
        TrieNode extension = TrieNodeFactory.CreateExtension([0x5], TrieNodeFactory.CreateLeaf(Nibbles(LargeKeyHex), SmallSlotValue));
        TreePath empty = TreePath.Empty;
        extension.ResolveKey(NullTrieNodeResolver.Instance, ref empty);

        TreePath path = TreePath.FromHexString("abcd");
        FlatEntryWriter.ExtensionInlineChildLeafEnumerator enumerator = new(ref path, extension);

        Assert.That(enumerator.MoveNext(), Is.False);
    }

    #endregion
}
