// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.Types;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Network
{
    [TestFixture]
    public class HobbitTests : HobbitTestsBase
    {
        [Test]
        public void Timeout_there_and_back([Values] StackType inbound, [Values] StackType outbound, [Values] bool framingEnabled)
        {
            using TimeoutMsg msg = new();
            msg.AdaptivePacketType = 242;
            msg.Timeout = XdcTestHelper.BuildSignedTimeout(Build.A.PrivateKey.TestObject, 123, 400);

            MessageSerializationService service = new(SerializerInfo.Create(new TimeoutMsgSerializer()));
            using DisposableByteBuffer dataBuffer = service.ZeroSerialize(msg).AsDisposable();
            Packet packet = new("eth", msg.AdaptivePacketType, dataBuffer.AsSpan().ToArray());

            Run(packet, inbound, outbound, framingEnabled);
        }

        [Test]
        public void Vote_there_and_back([Values] StackType inbound, [Values] StackType outbound, [Values] bool framingEnabled)
        {
            using VoteMsg msg = new();
            msg.AdaptivePacketType = XdcMessageCode.VoteMsg;
            msg.Vote = XdcTestHelper.BuildSignedVote(
                new BlockRoundInfo(Hash256.Zero, 123, 100), 400, Build.A.PrivateKey.TestObject);

            MessageSerializationService service = new(SerializerInfo.Create(new VoteMsgSerializer()));
            using DisposableByteBuffer dataBuffer = service.ZeroSerialize(msg).AsDisposable();
            Packet packet = new("eth", msg.AdaptivePacketType, dataBuffer.AsSpan().ToArray());

            Run(packet, inbound, outbound, framingEnabled);
        }

        [Test]
        public void SyncInfo_there_and_back([Values] StackType inbound, [Values] StackType outbound, [Values] bool framingEnabled)
        {
            using SyncInfoMsg msg = new();
            msg.AdaptivePacketType = XdcMessageCode.SyncInfoMsg;
            msg.SyncInfo = XdcTestHelper.BuildSyncInfo(Build.A.PrivateKey.TestObject, 123, 400);

            MessageSerializationService service = new(SerializerInfo.Create(new SyncInfoMsgSerializer()));
            using DisposableByteBuffer dataBuffer = service.ZeroSerialize(msg).AsDisposable();
            Packet packet = new("eth", msg.AdaptivePacketType, dataBuffer.AsSpan().ToArray());

            Run(packet, inbound, outbound, framingEnabled);
        }
    }
}
