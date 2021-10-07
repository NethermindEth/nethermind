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

using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Les
{
    [TestFixture]
    public class HelperTrieProofsMessageSerializerTests
    {
        [Test]
        public void RoundTrip()
        {
            byte[][] proofs = new byte[][]
            {
                TestItem.KeccakA.Bytes.ToArray(),
                TestItem.KeccakB.Bytes.ToArray(),
                TestItem.KeccakC.Bytes.ToArray(),
                TestItem.KeccakD.Bytes.ToArray(),
                TestItem.KeccakE.Bytes.ToArray(),
                TestItem.KeccakF.Bytes.ToArray(),
            };
            byte[][] auxData = new byte[][]
            {
                TestItem.KeccakG.Bytes.ToArray(),
                TestItem.KeccakH.Bytes.ToArray(),
                Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes,
            };
            var message = new HelperTrieProofsMessage(proofs, auxData, 324, 734);

            HelperTrieProofsMessageSerializer serializer = new HelperTrieProofsMessageSerializer();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
