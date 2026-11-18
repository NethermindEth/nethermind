// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class SnapBoundaryDeletionGateTests
{
    [TestCase("11" + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", false, TestName = "Extension subtree past the upper bound keeps its neighbours")]
    [TestCase("12" + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", true, TestName = "Extension compressed subtree reaches the upper bound")]
    [TestCase("1f" + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", true, TestName = "Extension prefix inside the upper bound")]
    public void Extension_is_cleaned_only_when_its_whole_subtree_is_inside_the_upper_bound(string upperBound, bool expected)
    {
        TrieNode extension = TrieNodeFactory.CreateExtension([2], new TrieNode(NodeType.Unknown, Keccak.Compute("child"u8)));

        bool canDelete = SnapProviderHelper.CanDeleteOutsideChildren(extension, TreePath.FromHexString("1"), new ValueHash256(Bytes.FromHexString(upperBound)));

        Assert.That(canDelete, Is.EqualTo(expected));
    }
}
