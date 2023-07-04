// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class BlockHeadersMessageSerializerTests
    {
        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void RoundTrip()
        {
            BlockHeader header = Build.A.BlockHeader.TestObject;
            header.ParentHash = Keccak.Zero;
            header.UnclesHash = Keccak.Zero;
            header.Beneficiary = Address.Zero;
            header.StateRoot = Keccak.Zero;
            header.TxRoot = Keccak.Zero;
            header.ReceiptsRoot = Keccak.Zero;
            header.Bloom = Bloom.Empty;
            header.Difficulty = 0x8ae;
            header.Number = 0xd05;
            header.GasLimit = 0x115c;
            header.GasUsed = 0x15b3;
            header.Timestamp = 0x1a0a;
            header.ExtraData = new byte[] { 0x77, 0x88 };
            header.MixHash = Keccak.Zero;
            header.Nonce = 0;
            header.Hash = new Keccak("0x8c2f2af15b7b563b6ab1e09bed0e9caade7ed730aec98b70a993597a797579a9");

            var ethMessage = new Network.P2P.Subprotocols.Eth.V62.Messages.BlockHeadersMessage();
            ethMessage.BlockHeaders = new[] { header };

            BlockHeadersMessage message = new(1111, ethMessage);

            BlockHeadersMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "f90202820457f901fcf901f9a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000000000000000940000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000000000000000b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008208ae820d0582115c8215b3821a0a827788a00000000000000000000000000000000000000000000000000000000000000000880000000000000000");
        }

    }
}
