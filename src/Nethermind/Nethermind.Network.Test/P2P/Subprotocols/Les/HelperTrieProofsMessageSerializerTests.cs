// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
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
                TestItem.KeccakA.ToByteArray(),
                TestItem.KeccakB.ToByteArray(),
                TestItem.KeccakC.ToByteArray(),
                TestItem.KeccakD.ToByteArray(),
                TestItem.KeccakE.ToByteArray(),
                TestItem.KeccakF.ToByteArray(),
            };
            byte[][] auxData = new byte[][]
            {
                TestItem.KeccakG.ToByteArray(),
                TestItem.KeccakH.ToByteArray(),
                Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes,
            };
            var message = new HelperTrieProofsMessage(proofs, auxData, 324, 734);

            HelperTrieProofsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
