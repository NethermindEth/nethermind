// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using DotNetty.Buffers;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class MessageSerializationServiceTests
{
    [Test]
    public void Serialize_rejects_a_write_of_other_than_the_reported_length([Values(-1, 1)] int lengthError)
    {
        MessageSerializationService service = new(SerializerInfo.Create(new SpanSerializer { LengthError = lengthError }));

        Assert.That(() => service.Serialize(new SpanMessage()), Throws.InvalidOperationException);
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
