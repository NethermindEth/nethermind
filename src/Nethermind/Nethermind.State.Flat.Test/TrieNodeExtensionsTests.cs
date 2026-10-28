// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class TrieNodeExtensionsTests
{
    private static readonly Hash256 Hash = TestItem.KeccakA;

    private static readonly byte[] LeafRlp = [0xc2, 0x20, 0x01];

    // Both halves of the predicate carry weight, so every combination is pinned: relaxing it to either operand
    // alone would let a real node be skipped by persistence and the snapshot builders.
    [TestCase(NodeType.Unknown, false, ExpectedResult = true, TestName = "hash_only_reference_is_a_placeholder")]
    [TestCase(NodeType.Unknown, true, ExpectedResult = false, TestName = "undecoded_node_carrying_rlp_is_not")]
    [TestCase(NodeType.Leaf, false, ExpectedResult = false, TestName = "decoded_node_without_rlp_is_not")]
    [TestCase(NodeType.Leaf, true, ExpectedResult = false, TestName = "decoded_node_with_rlp_is_not")]
    public bool IsHashOnlyPlaceholder_pins_both_operands(NodeType nodeType, bool withRlp) =>
        BuildNode(nodeType, withRlp).IsHashOnlyPlaceholder();

    private static TrieNode BuildNode(NodeType nodeType, bool withRlp) => (nodeType, withRlp) switch
    {
        (NodeType.Unknown, false) => new TrieNode(NodeType.Unknown, Hash),
        (NodeType.Unknown, true) => new TrieNode(NodeType.Unknown, Hash, LeafRlp),
        (_, false) => new TrieNode(nodeType),
        (_, true) => new TrieNode(nodeType, LeafRlp)
    };
}
