// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.P2P;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Network
{
    [TestFixture]
    public class HobbitTests: HobbitTestsBase
    {
        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Timeout_there_and_back(StackType inbound, StackType outbound, bool framingEnabled)
        {
            using TimeoutMsg msg = new();
            msg.AdaptivePacketType = 242;
            msg.Timeout = XdcTestHelper.BuildSignedTimeout(Build.A.PrivateKey.TestObject, 123, 400);

            MessageSerializationService service = new(SerializerInfo.Create(new TimeoutMsgSerializer()));
            byte[] data = service.ZeroSerialize(msg).AsSpan().ToArray();
            Packet packet = new("eth", msg.AdaptivePacketType, data);

            Run(packet, inbound, outbound, framingEnabled);
        }
    }
}
