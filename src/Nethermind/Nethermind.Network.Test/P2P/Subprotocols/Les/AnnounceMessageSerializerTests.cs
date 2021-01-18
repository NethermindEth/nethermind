//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Les;
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
            AnnounceMessage announceMessage = new AnnounceMessage();
            announceMessage.HeadHash = Keccak.Compute("1");
            announceMessage.HeadBlockNo = 4;
            announceMessage.TotalDifficulty = 131200;
            announceMessage.ReorgDepth = 0;

            AnnounceMessageSerializer serializer = new AnnounceMessageSerializer();

            SerializerTester.TestZero(serializer, announceMessage, "e8a0c89efdaa54c0f20c7adf612882df0950f5a951637e0307cdcb4c672f298b8bc6048302008080c0");
        }
    }
}
