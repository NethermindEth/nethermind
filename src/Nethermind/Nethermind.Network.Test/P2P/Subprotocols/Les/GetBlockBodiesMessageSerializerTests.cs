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
    public class GetBlockBodiesMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.GetBlockBodiesMessage(Keccak.OfAnEmptySequenceRlp, Keccak.Zero, Keccak.EmptyTreeHash);
            var message = new GetBlockBodiesMessage(ethMessage, 1);

            GetBlockBodiesMessageSerializer serializer = new GetBlockBodiesMessageSerializer();

            SerializerTester.TestZero(serializer, message, "f86601f863a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347a00000000000000000000000000000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421");
        }
    }
}
