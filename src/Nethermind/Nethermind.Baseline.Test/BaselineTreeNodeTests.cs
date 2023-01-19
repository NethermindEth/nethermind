// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Baseline.Tree;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [TestFixture]
    public class BaselineTreeNodeTests
    {
        [Test]
        public void On_creation_sets_the_fields_properly()
        {
            byte[] bytes = new byte[32];
            bytes[1] = 44;
            BaselineTreeNode treeNode = new BaselineTreeNode(new Keccak(bytes), 5);
            treeNode.Hash.Should().Be(new Keccak(bytes));
            treeNode.NodeIndex.Should().Be(5);
        }
    }
}
