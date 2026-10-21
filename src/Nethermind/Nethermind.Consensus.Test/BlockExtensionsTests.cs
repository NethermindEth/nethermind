// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Consensus.Processing;
using NUnit.Framework;
using System.Collections.Generic;
using System.Text;
using Nethermind.Config;

namespace Nethermind.Consensus.Test
{
    public class BlockExtensionsTests
    {
        private static IEnumerable<TestCaseData> IsByNethermindNodeCases()
        {
            yield return new TestCaseData(Encoding.ASCII.GetBytes(BlocksConfig.DefaultExtraData)).Returns(true).SetName("Default extra data");
            yield return new TestCaseData(Encoding.ASCII.GetBytes("helloNeThErMiNdWorld!")).Returns(true).SetName("Contains Nethermind");
            yield return new TestCaseData(new byte[] { 1, 2, 3 }).Returns(false).SetName("Random bytes");
            yield return new TestCaseData(null).Returns(false).SetName("Null extra data");
        }

        [TestCaseSource(nameof(IsByNethermindNodeCases))]
        public bool Is_by_nethermind_node(byte[] extraData)
        {
            Block block = Build.A.Block.WithExtraData(extraData).TestObject;
            return block.IsByNethermindNode();
        }
    }
}
