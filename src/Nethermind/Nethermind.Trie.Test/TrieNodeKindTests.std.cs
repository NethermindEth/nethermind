// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public partial class TrieNodeKindTests
{
    // Encoding hashes through the zkVM keccak precompile on the guest build, which a host test run cannot load.
    [Test]
    public void Kind_properties_agree_with_node_type_after_decoding([Values(NodeType.Branch, NodeType.Extension, NodeType.Leaf)] NodeType nodeType)
    {
        TreePath path = TreePath.Empty;
        TrieNode node = new(NodeType.Unknown, Create(nodeType).RlpEncode(NullTrieNodeResolver.Instance, ref path));
        node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        AssertKind(node, nodeType);
    }
}
