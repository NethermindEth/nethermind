// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
//using Nethermind.Core2.Types;
using Nethermind.Merkleization;
using NUnit.Framework;

namespace Nethermind.Serialization.Ssz.Test
{
    [TestFixture]
    public class MerkleTreeNodeTests
    {
        [Test]
        public void On_creation_sets_the_fields_properly()
        {
            byte[] bytes = new byte[32];
            bytes[1] = 44;
            Bytes32 hash = Bytes32.Wrap(bytes);
            MerkleTreeNode merkleTreeNode = new MerkleTreeNode(hash, 5);
            merkleTreeNode.Hash.Should().Be(hash);
            merkleTreeNode.Index.Should().Be(5);
        }
    }
}
