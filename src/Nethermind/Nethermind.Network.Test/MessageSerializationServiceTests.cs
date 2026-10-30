// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class MessageSerializationServiceTests
{
    [Test]
    public void Serialize_rejects_a_write_of_other_than_the_reported_length([Values(-1, 1)] int lengthError, [Values] bool toByteBuffer)
    {
        MessageSerializationService service = new(SerializerInfo.Create(new SpanSerializer { LengthError = lengthError }));
        SpanMessage message = new();
        IByteBuffer? rented = null;
        IByteBufferAllocator allocator = Substitute.For<IByteBufferAllocator>();
        allocator.Buffer(Arg.Any<int>()).Returns(c => rented = UnpooledByteBufferAllocator.Default.Buffer(c.Arg<int>()));

        Action serialize = toByteBuffer ? () => service.ZeroSerialize(message, allocator) : () => service.Serialize(message);
        Assert.That(serialize, Throws.InvalidOperationException.With.Message.Contains("wrote"));
        if (toByteBuffer)
            Assert.That(rented!.ReferenceCount, Is.Zero);
    }

    [Test]
    public void Deserialize_from_a_byte_buffer_reads_the_rest_of_it_with_a_span_serializer()
    {
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        using DisposableByteBuffer packet = UnpooledByteBufferAllocator.Default.Buffer().AsDisposable();
        packet.WriteBytes(Bytes.FromHexString("01c104"));
        packet.SkipBytes(1);

        DisconnectMessage message = container.Resolve<IMessageSerializationService>().Deserialize<DisconnectMessage>(packet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.Reason, Is.EqualTo((int)EthDisconnectReason.TooManyPeers));
            Assert.That(packet.IsReadable(), Is.False);
        }
    }

    [Test]
    public void Reports_a_missing_serializer_of_either_kind([Values] bool serialize)
    {
        MessageSerializationService service = new();

        Action use = serialize ? () => service.ZeroSerialize(new SpanMessage()) : () => service.Deserialize<SpanMessage>(Array.Empty<byte>());
        Assert.That(use, Throws.InvalidOperationException.With.Message.EqualTo($"No serializer registered for {nameof(SpanMessage)}."));
    }

    [TestCaseSource(nameof(P2PMessages))]
    public void ZeroSerialize_writes_the_packet_type_then_the_message(Func<IMessageSerializationService, IByteBuffer> zeroSerialize, string expected)
    {
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();

        using DisposableByteBuffer packet = zeroSerialize(container.Resolve<IMessageSerializationService>()).AsDisposable();

        Assert.That(packet.ReadAllHex(), Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> P2PMessages()
    {
        yield return Case("Hello", new HelloMessage
        {
            P2PVersion = 5,
            ClientId = "Nethermind",
            ListenPort = 30303,
            NodeId = TestItem.PublicKeyA,
            Capabilities = new ArrayPoolList<Capability>(2) { new("ETH", 68), new("snap", 1) }
        }, "80f85f058a4e65746865726d696e64cdc58365746844c684736e61700182765fb840a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92");
        yield return Case("AddCapability", new AddCapabilityMessage(new Capability("Snap", 1)), "04c684736e617001");
        yield return Case("Disconnect", new DisconnectMessage(EthDisconnectReason.TooManyPeers), "01c104");
        yield return Case("Ping", PingMessage.Instance, "02c0");
        yield return Case("Pong", PongMessage.Instance, "03c0");

        static TestCaseData Case<T>(string name, T message, string expected) where T : P2PMessage
        {
            message.AdaptivePacketType = message.PacketType;
            Func<IMessageSerializationService, IByteBuffer> zeroSerialize = service => service.ZeroSerialize(message);
            return new TestCaseData(zeroSerialize, expected).SetArgDisplayNames(name);
        }
    }

    [Test]
    public void AddMessageSerializer_registers_the_interface_the_serializer_implements()
    {
        using IContainer container = new ContainerBuilder()
            .AddMessageSerializer<SpanMessage, SpanSerializer>()
            .AddMessageSerializer<ZeroMessage, ZeroSerializer>()
            .Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<IMessageSerializer<SpanMessage>>(), Is.InstanceOf<SpanSerializer>());
            Assert.That(container.Resolve<IZeroMessageSerializer<ZeroMessage>>(), Is.InstanceOf<ZeroSerializer>());
        }
    }

    private sealed class SpanMessage : MessageBase;

    private sealed class ZeroMessage : MessageBase;

    private sealed class SpanSerializer : IMessageSerializer<SpanMessage>
    {
        private const int Length = 8;

        public int LengthError { get; init; }

        public int GetLength(SpanMessage message) => Length;

        public int Serialize(Span<byte> destination, SpanMessage message) => Length + LengthError;

        public SpanMessage Deserialize(ReadOnlySpan<byte> data) => new();
    }

    private sealed class ZeroSerializer : IZeroMessageSerializer<ZeroMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, ZeroMessage message) { }

        public ZeroMessage Deserialize(IByteBuffer byteBuffer) => new();
    }
}
