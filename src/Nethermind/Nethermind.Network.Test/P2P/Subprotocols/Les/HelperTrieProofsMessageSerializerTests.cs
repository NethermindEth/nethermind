// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
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
                TestItem._commitmentA.BytesToArray(),
                TestItem._commitmentB.BytesToArray(),
                TestItem._commitmentC.BytesToArray(),
                TestItem._commitmentD.BytesToArray(),
                TestItem._commitmentE.BytesToArray(),
                TestItem._commitmentF.BytesToArray(),
            };
            byte[][] auxData = new byte[][]
            {
                TestItem._commitmentG.BytesToArray(),
                TestItem._commitmentH.BytesToArray(),
                Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes,
            };
            var message = new HelperTrieProofsMessage(proofs, auxData, 324, 734);

            HelperTrieProofsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message);
        }
    }
}
