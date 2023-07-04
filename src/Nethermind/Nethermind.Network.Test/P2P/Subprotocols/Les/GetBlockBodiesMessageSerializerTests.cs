// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
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
            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockBodiesMessage(Keccak.OfAnEmptySequenceRlp, Keccak.Zero, Keccak.EmptyTreeHash);
            var message = new GetBlockBodiesMessage(ethMessage, 1);

            GetBlockBodiesMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "f86601f863a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347a00000000000000000000000000000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421");
        }
    }
}
