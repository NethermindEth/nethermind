// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class AnnounceMessageSerializerTests
    {
        [Test]
        public void RoundTripWithRequiredData()
        {
            AnnounceMessage announceMessage = new();
            announceMessage.HeadHash = Keccak.Compute("1");
            announceMessage.HeadBlockNo = 4;
            announceMessage.TotalDifficulty = 131200;
            announceMessage.ReorgDepth = 0;

            AnnounceMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, announceMessage, "e8a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6048302008080c0");
        }
    }
}
