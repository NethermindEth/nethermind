///  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class GetContractCodesMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            CodeRequest[] requests = new CodeRequest[]
            {
                new CodeRequest(TestItem.KeccakA, TestItem.KeccakB),
                new CodeRequest(TestItem.KeccakC, TestItem.KeccakD),
            };

            GetContractCodesMessage message = new GetContractCodesMessage(requests, 774);

            GetContractCodesMessageSerializer serializer = new GetContractCodesMessageSerializer();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
