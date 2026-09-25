// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;
using InnerMessage = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.GetPooledTransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66
{
    [TestFixture]
    public class GetPooledTransactionsSerializerTests
    {
        [Test]
        public void Trailing_data_disposes_decoded_message([Values(false, true)] bool trailingData)
        {
            IOwnedReadOnlyList<ValueHash256> hashes = Substitute.For<IOwnedReadOnlyList<ValueHash256>>();
            InnerMessage inner = new(hashes);
            IZeroInnerMessageSerializer<InnerMessage> innerSerializer = Substitute.For<IZeroInnerMessageSerializer<InnerMessage>>();
            innerSerializer.Deserialize(Arg.Any<IByteBuffer>()).Returns(call =>
            {
                call.Arg<IByteBuffer>().SkipBytes(1);
                return inner;
            });
            TrackingSerializer serializer = new(innerSerializer);
            using DisposableByteBuffer buffer = Unpooled.WrappedBuffer(Convert.FromHexString(trailingData ? "c301c000" : "c201c0")).AsDisposable();

            if (trailingData)
            {
                Assert.Throws<RlpException>(() => serializer.Deserialize(buffer));
            }
            else
            {
                GetPooledTransactionsMessage message = serializer.Deserialize(buffer);
                hashes.DidNotReceive().Dispose();
                message.Dispose();
            }

            hashes.Received(1).Dispose();
        }

        private sealed class TrackingSerializer(IZeroInnerMessageSerializer<InnerMessage> innerSerializer)
            : Eth66MessageSerializer<GetPooledTransactionsMessage, InnerMessage>(innerSerializer);

        //test from https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
        [Test]
        public void Roundtrip()
        {
            ValueHash256 a = new("0x00000000000000000000000000000000000000000000000000000000deadc0de");
            ValueHash256 b = new("0x00000000000000000000000000000000000000000000000000000000feedbeef");
            ValueHash256[] keys = { a, b };

            using GetPooledTransactionsMessage message = new(keys.ToPooledList()) { RequestId = 1111 };

            GetPooledTransactionsMessageSerializer serializer = new();

            SerializerTester.TestZero(serializer, message, "f847820457f842a000000000000000000000000000000000000000000000000000000000deadc0dea000000000000000000000000000000000000000000000000000000000feedbeef");
        }
    }
}
