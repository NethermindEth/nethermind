// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class GetBlockHeadersMessageSerializerTests
    {
        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void RoundTrip_number()
        {
            var ethMessage =
                new Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockHeadersMessage
                {
                    StartBlockHash = null,
                    StartBlockNumber = 9999,
                    MaxHeaders = 5,
                    Skip = 5,
                    Reverse = 0
                };

            var message = new GetBlockHeadersMessage(1111, ethMessage);

            GetBlockHeadersMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "ca820457c682270f050580");
        }

        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void RoundTrip_hash()
        {
            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockHeadersMessage
            {
                StartBlockHash = new Keccak("0x00000000000000000000000000000000000000000000000000000000deadc0de"),
                StartBlockNumber = 0,
                MaxHeaders = 5,
                Skip = 5,
                Reverse = 0
            };

            var message = new GetBlockHeadersMessage(1111, ethMessage);

            GetBlockHeadersMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "e8820457e4a000000000000000000000000000000000000000000000000000000000deadc0de050580");
        }
    }
}
