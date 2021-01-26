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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class GetHelperTrieProofsMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            HelperTrieRequest[] requests = new HelperTrieRequest[]
            {
                new HelperTrieRequest(HelperTrieType.CHT, 177, TestItem.RandomDataA, 2, 1),
                new HelperTrieRequest(HelperTrieType.BloomBits, 77, TestItem.RandomDataB, 4, 0),
            };
            GetHelperTrieProofsMessage message = new GetHelperTrieProofsMessage();
            message.RequestId = 100;
            message.Requests = requests;

            GetHelperTrieProofsMessageSerializer serializer = new GetHelperTrieProofsMessageSerializer();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
