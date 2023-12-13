// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Wit.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Wit
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class MessageTests
    {
        [Test]
        public void Message_code_is_correct_in_request()
        {
            new GetBlockWitnessHashesMessage(1, Keccak.Zero).PacketType.Should().Be(1);
        }

        [Test]
        public void Message_code_is_correct_in_response()
        {
            new BlockWitnessHashesMessage(1, null).PacketType.Should().Be(2);
        }
    }

    [TestFixture, Parallelizable(ParallelScope.All)]
    public class GetBlockWitnessHashesMessageSerializerTests
    {
        [Test]
        public void Roundtrip_init()
        {
            GetBlockWitnessHashesMessageSerializer serializer = new();
            GetBlockWitnessHashesMessage message = new(1, Keccak.Zero);
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_handle_null()
        {
            GetBlockWitnessHashesMessageSerializer serializer = new();
            GetBlockWitnessHashesMessage message = new(1, null);
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_deserialize_trinity()
        {
            GetBlockWitnessHashesMessageSerializer serializer = new();
            var trinityBytes = Bytes.FromHexString("0xea880ea29ca8028d7edea04bf6040124107de018c753ff2a9e464ca13e9d099c45df6a48ddbf436ce30c83");
            var buffer = ByteBufferUtil.DefaultAllocator.Buffer(trinityBytes.Length);
            buffer.WriteBytes(trinityBytes);
            GetBlockWitnessHashesMessage msg =
                ((IZeroMessageSerializer<GetBlockWitnessHashesMessage>)serializer).Deserialize(buffer);
        }
    }
}
