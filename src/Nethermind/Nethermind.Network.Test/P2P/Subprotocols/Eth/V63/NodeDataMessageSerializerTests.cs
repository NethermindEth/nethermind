// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class NodeDataMessageSerializerTests
    {
        private static void Test(IOwnedReadOnlyList<byte[]>? data, string? expected = null)
        {
            using NodeDataMessage message = new(data is not null ? new ByteArrayListAdapter(data) : null);

            NodeDataMessageSerializer serializer = new();
            SerializerTester.Test(serializer, message, expected);
        }

        [Test]
        public void Roundtrip()
        {
            using ArrayPoolList<byte[]> data = new(3) { TestItem.KeccakA.BytesToArray(), TestItem.KeccakB.BytesToArray(), TestItem.KeccakC.BytesToArray() };
            Test(data, EthSerializerGoldens.KeccakAbcListRlp);
        }

        [Test]
        public void Roundtrip_with_null_top_level() => Test(null, EthSerializerGoldens.EmptyListRlp);

        private static IEnumerable<TestCaseData> DataLimitCases() =>
            ByteArrayListLimitTester.BoundaryCases(NodeDataMessageSerializer.RlpLimit);

        [TestCaseSource(nameof(DataLimitCases))]
        public void Deserialize_EnforcesDataCountLimit(int dataCount, bool shouldThrow) =>
            ByteArrayListLimitTester.AssertLimitEnforced(
                new NodeDataMessageSerializer(),
                static data => new NodeDataMessage(data),
                static message => message.Data.Count,
                dataCount,
                shouldThrow);

        [Test]
        public void Deserializing_from_a_packet_keeps_the_entries_in_it([Values] bool eth66, [Values] bool trailingByte)
        {
            byte[] entry = TestItem.KeccakA.BytesToArray();
            IMessageSerializationService service = new SerializationBuilder()
                .With(new NodeDataMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessageSerializer())
                .TestObject;
            IByteBuffer packet = eth66
                ? Serialize(new Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage(1, NodeData(entry)))
                : Serialize(NodeData(entry));

            if (trailingByte)
            {
                packet.WriteByte(0);
                Assert.Throws<RlpReaderException>(() => Deserialize());
                Assert.That(packet.ReferenceCount, Is.EqualTo(1), "packet released with the rejected message");
                packet.Release();
                return;
            }

            NodeDataMessage deserialized = Deserialize();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(deserialized.Data[0].ToArray(), Is.EqualTo(entry));
                Assert.That(deserialized.Data[0].Overlaps(packet.Array), Is.True, "entry points into the packet");
                Assert.That(packet.ReferenceCount, Is.EqualTo(2), "packet held by the message");
            }

            deserialized.Dispose();
            Assert.That(packet.ReferenceCount, Is.EqualTo(1), "packet released with the message");
            packet.Release();

            static NodeDataMessage NodeData(byte[] entry) => new(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { entry }));

            NodeDataMessage Deserialize() => eth66
                ? service.Deserialize<Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage>(packet).EthMessage
                : service.Deserialize<NodeDataMessage>(packet);

            IByteBuffer Serialize<T>(T message) where T : P2PMessage
            {
                using (message)
                {
                    IByteBuffer serialized = service.ZeroSerialize(message);
                    serialized.SkipBytes(1);
                    return serialized;
                }
            }
        }

        [Test]
        public void Roundtrip_with_empty_entry()
        {
            using ArrayPoolList<byte[]> data = new(3) { TestItem.KeccakA.BytesToArray(), Array.Empty<byte>(), TestItem.KeccakC.BytesToArray() };
            // Three items: 0xa0 + 32 bytes, 0x80 (empty), 0xa0 + 32 bytes - a 67-byte list payload (0xf8 0x43).
            Test(data, "f843a003783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b76080a0017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72");
        }
    }
}
