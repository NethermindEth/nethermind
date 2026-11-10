// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Modules;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class MessageSerializationServiceTests
{
    [Test]
    public void Serialize_rejects_a_write_of_other_than_the_reported_length([Values(-1, 1)] int lengthError, [Values] bool intoSpan)
    {
        MessageSerializationService service = new(SerializerInfo.Create(new SpanSerializer { LengthError = lengthError }));
        SpanMessage message = new();

        Action serialize = intoSpan ? () => service.Serialize(message, new byte[service.GetLength(message)]) : () => service.Serialize(message);
        Assert.That(serialize, Throws.InvalidOperationException.With.Message.Contains("wrote"));
    }

    [Test]
    public void Deserialize_reads_the_readable_bytes_of_a_byte_buffer_packet()
    {
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        using DisposableByteBuffer buffer = UnpooledByteBufferAllocator.Default.Buffer().AsDisposable();
        buffer.WriteBytes(Bytes.FromHexString("01c104"));
        buffer.SkipBytes(1);

        DisconnectMessage message = container.Resolve<IMessageSerializationService>().Deserialize<DisconnectMessage>(new NettyBufferPacket(buffer));

        Assert.That(message.Reason, Is.EqualTo((int)EthDisconnectReason.TooManyPeers));
    }

    [Test]
    public void Reports_a_missing_serializer_of_either_kind([Values] bool serialize)
    {
        MessageSerializationService service = new();

        Action use = serialize ? () => service.GetLength(new SpanMessage()) : () => service.Deserialize<SpanMessage>(Array.Empty<byte>());
        Assert.That(use, Throws.InvalidOperationException.With.Message.EqualTo($"No serializer registered for {nameof(SpanMessage)}."));
    }

    [Test]
    public void Rejects_a_serializer_of_another_message_type() =>
        Assert.That(() => new MessageSerializationService(new SerializerInfo(typeof(PingMessage), new SpanSerializer())),
            Throws.ArgumentException.With.Message.EqualTo($"Serializer of type {nameof(SpanSerializer)} must implement IMessageSerializer<{nameof(PingMessage)}>."));

    [Test]
    public void AddMessageSerializer_registers_the_serializer_as_its_interface()
    {
        using IContainer container = new ContainerBuilder()
            .AddMessageSerializer<SpanMessage, SpanSerializer>()
            .Build();

        Assert.That(container.Resolve<IMessageSerializer<SpanMessage>>(), Is.InstanceOf<SpanSerializer>());
    }

    private sealed class SpanMessage : MessageBase;

    private sealed class SpanSerializer : IMessageSerializer<SpanMessage>
    {
        private const int Length = 8;

        public int LengthError { get; init; }

        public int GetLength(SpanMessage message) => Length;

        public int Serialize(Span<byte> destination, SpanMessage message) => Length + LengthError;

        public SpanMessage Deserialize(ReadOnlySpan<byte> data) => new();
    }
}
