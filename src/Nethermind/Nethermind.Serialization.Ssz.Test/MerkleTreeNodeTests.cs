// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            MerkleTreeNode merkleTreeNode = new(hash, 5);
            Assert.That(merkleTreeNode.Hash, Is.EqualTo(hash));
            Assert.That(merkleTreeNode.Index, Is.EqualTo(5));
        }
    }
}
