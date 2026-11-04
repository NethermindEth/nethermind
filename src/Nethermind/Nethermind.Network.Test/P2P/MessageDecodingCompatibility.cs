// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P;

/// <summary>
/// Checks which payloads the protocol handlers accept, as decoded through an <see cref="IMessageSerializationService"/>.
/// </summary>
/// <remarks>
/// <see cref="MessageSerializationService"/> cannot tell how much of a payload an <see cref="IMessageSerializer{T}"/> read, so
/// each such serializer must reject bytes left after its message itself. Every one a module registers needs a sample.
/// </remarks>
public static class MessageDecodingCompatibility
{
    /// <param name="AcceptsTrailingByte">Whether the handlers accept the message with a byte after it, as they always have.</param>
    public sealed record Sample(
        Type MessageType,
        Func<IMessageSerializationService, byte[]> Payload,
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> Deserialize,
        bool AcceptsTrailingByte)
    {
        public static Sample Of<T>(Func<T> create, bool acceptsTrailingByte = false) where T : P2PMessage =>
            new(typeof(T), service => Serialize(service, create()), Deserializer<T>(), acceptsTrailingByte);

        public override string ToString() => $"{MessageType.Namespace!.Split('.')[^2]}.{MessageType.Name}";

        private static byte[] Serialize<T>(IMessageSerializationService service, T message) where T : P2PMessage
        {
            using (message)
            {
                using DisposableByteBuffer packet = service.ZeroSerialize(message).AsDisposable();
                packet.SkipBytes(Rlp.LengthOf(message.AdaptivePacketType));
                return packet.ReadAllBytesAsArray();
            }
        }
    }

    public static Func<IMessageSerializationService, IByteBuffer, P2PMessage> Deserializer<T>() where T : P2PMessage =>
        static (service, buffer) => service.Deserialize<T>(buffer);

    /// <summary>
    /// Asserts every <see cref="P2PMessage"/> with a registered <see cref="IMessageSerializer{T}"/> has one of <paramref name="samples"/>.
    /// </summary>
    public static void AssertSampled(IEnumerable<SerializerInfo> registered, IEnumerable<Sample> samples)
    {
        Type[] spanSerialized = registered
            .Where(static info => typeof(P2PMessage).IsAssignableFrom(info.MessageType)
                && typeof(IMessageSerializer<>).MakeGenericType(info.MessageType).IsInstanceOfType(info.Serializer))
            .Select(static info => info.MessageType)
            .ToArray();

        Assert.That(spanSerialized, Is.SubsetOf(samples.Select(static sample => sample.MessageType)));
    }

    public static void AssertTrailingByteHandling(IMessageSerializationService service, Sample sample, bool trailingByte)
    {
        byte[] payload = sample.Payload(service);
        if (trailingByte) payload = [.. payload, 0];

        Assert.That(IsAccepted(service, sample.Deserialize, payload), Is.EqualTo(!trailingByte || sample.AcceptsTrailingByte));
    }

    /// <summary>
    /// Mirrors <c>ProtocolHandlerBase.Deserialize</c>, which accepts a message only if decoding it reads the whole payload.
    /// </summary>
    public static bool IsAccepted(IMessageSerializationService service, Func<IMessageSerializationService, IByteBuffer, P2PMessage> deserialize, byte[] payload)
    {
        IByteBuffer buffer = Unpooled.Buffer(payload.Length);
        buffer.WriteBytes(payload);
        try
        {
            using P2PMessage message = deserialize(service, buffer);
            return !buffer.IsReadable();
        }
        catch (Exception exception) when (exception is not AssertionException)
        {
            return false;
        }
        finally
        {
            buffer.Release();
        }
    }
}
