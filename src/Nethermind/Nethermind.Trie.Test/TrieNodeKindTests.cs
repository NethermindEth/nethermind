// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

/// <summary>The node-kind properties against <see cref="TrieNode.NodeType"/>.</summary>
/// <remarks>
/// They are type tests on the node data rather than <see cref="NodeType"/> compares, and the guest
/// has its own forms in <c>TrieNode.zkevm.cs</c>, so <c>Nethermind.Trie.ZkEvm.Test</c> links this file
/// to run it against those as well.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class TrieNodeKindTests
{
    [Test]
    public void Kind_properties_agree_with_node_type([Values] NodeType nodeType) => AssertKind(Create(nodeType), nodeType);

#if !ZK_EVM
    // Encoding hashes through the zkVM keccak precompile on the guest build, which a host test run cannot load.
    [Test]
    public void Kind_properties_agree_with_node_type_after_decoding([Values(NodeType.Branch, NodeType.Extension, NodeType.Leaf)] NodeType nodeType)
    {
        TreePath path = TreePath.Empty;
        TrieNode node = new(NodeType.Unknown, Create(nodeType).RlpEncode(NullTrieNodeResolver.Instance, ref path));
        node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        AssertKind(node, nodeType);
    }
#endif

    private static void AssertKind(TrieNode node, NodeType nodeType)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.NodeType, Is.EqualTo(nodeType));
            Assert.That(node.IsBranch, Is.EqualTo(nodeType == NodeType.Branch));
            Assert.That(node.IsExtension, Is.EqualTo(nodeType == NodeType.Extension));
            Assert.That(node.IsLeaf, Is.EqualTo(nodeType == NodeType.Leaf));
        }
    }

    private static TrieNode Create(NodeType nodeType) => nodeType switch
    {
        NodeType.Branch => TrieNodeFactory.CreateBranch(),
        NodeType.Extension => TrieNodeFactory.CreateExtension(new byte[] { 1, 2, 3 }, CreateLeaf()),
        NodeType.Leaf => CreateLeaf(),
        _ => new TrieNode(NodeType.Unknown),
    };

    private static TrieNode CreateLeaf() => TrieNodeFactory.CreateLeaf(new byte[] { 4, 5, 6 }, new CappedArray<byte>(new byte[] { 7 }));
}
