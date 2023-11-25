// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Consensus.Processing;
using NUnit.Framework;
using System.Text;
using Nethermind.Config;

namespace Nethermind.Consensus.Test
{
    public class BlockExtenstionsTests
    {
        [Test]
        public void Is_by_nethermind_node()
        {
            Block defaultBlock = Build.A.Block.WithExtraData(Encoding.ASCII.GetBytes(BlocksConfig.DefaultExtraData)).TestObject;
            Block containsNethermindBlock = Build.A.Block.WithExtraData(Encoding.ASCII.GetBytes("helloNeThErMiNdWorld!")).TestObject;
            Block randomExtraDataBlock = Build.A.Block.WithExtraData(new byte[] { 1, 2, 3 }).TestObject;
            Block nullExtraDataBlock = Build.A.Block.WithExtraData(null).TestObject;
            
            Assert.That(defaultBlock.IsByNethermindNode());
            Assert.That(containsNethermindBlock.IsByNethermindNode());
            Assert.That(!randomExtraDataBlock.IsByNethermindNode());
            Assert.That(!nullExtraDataBlock.IsByNethermindNode());
        }
    }
}