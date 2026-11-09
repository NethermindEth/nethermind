// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network;

public class MessageSerializationService : IMessageSerializationService
{
    private readonly ConcurrentDictionary<RuntimeTypeHandle, object> _serializers = new();

    public MessageSerializationService(params IReadOnlyList<SerializerInfo> serializers)
    {
        foreach ((Type MessageType, object Serializer) in serializers)
        {
            Type serializerInterface = typeof(IMessageSerializer<>).MakeGenericType(MessageType);

            if (!serializerInterface.IsAssignableFrom(Serializer.GetType()))
            {
                ThrowInvalidSerializer(Serializer, MessageType);
            }

            _serializers[MessageType.TypeHandle] = Serializer;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidSerializer(object serializer, Type messageType)
            => throw new ArgumentException($"Serializer of type {serializer.GetType().Name} must implement {nameof(IMessageSerializer<>)}<{messageType.Name}>.");
    }

    public ArrayPoolSpan<byte> Serialize<T>(T message) where T : MessageBase
    {
        IMessageSerializer<T> serializer = GetSerializer<T>();
        ArrayPoolSpan<byte> buffer = new(serializer.GetLength(message));
        try
        {
            SerializeExactly(serializer, buffer, message);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase => GetSerializer<T>().Deserialize(bytes);

    public T Deserialize<T>(IByteBuffer buffer) where T : MessageBase
    {
        IMessageSerializer<T> serializer = GetSerializer<T>();
        T message = serializer is IOwningMessageSerializer<T> owningSerializer
            ? DeserializeOwned(owningSerializer, buffer)
            : serializer.Deserialize(buffer.AsSpan());
        // The serializer does not report how much it read, so the whole message counts as read.
        buffer.SetReaderIndex(buffer.WriterIndex);
        return message;
    }

    private static T DeserializeOwned<T>(IOwningMessageSerializer<T> serializer, IByteBuffer buffer) where T : MessageBase
    {
        NettyBufferMemoryOwner owner = new(buffer);
        // The owner's memory follows the reader index, which moves once the message is read.
        return serializer.Deserialize(owner, owner.Memory);
    }

    public IByteBuffer ZeroSerialize<T>(T message, IByteBufferAllocator? allocator = null) where T : MessageBase
        => ZeroSerialize(GetSerializer<T>(), message, allocator ?? NethermindBuffers.Default);

    private static IByteBuffer ZeroSerialize<T>(IMessageSerializer<T> serializer, T message, IByteBufferAllocator allocator) where T : MessageBase
    {
        P2PMessage? p2PMessage = message as P2PMessage;
        int packetTypeLength = p2PMessage is null ? 0 : Rlp.LengthOf(p2PMessage.AdaptivePacketType);
        int length = packetTypeLength + serializer.GetLength(message);
        IByteBuffer byteBuffer = allocator.Buffer(length);
        try
        {
            // DotNetty allocators return array-backed buffers, the direct ones included.
            Span<byte> packet = byteBuffer.Array.AsSpan(byteBuffer.ArrayOffset + byteBuffer.WriterIndex, length);
            if (p2PMessage is not null)
            {
                RlpWriter writer = new(packet);
                writer.Encode(p2PMessage.AdaptivePacketType);
            }

            SerializeExactly(serializer, packet[packetTypeLength..], message);
            byteBuffer.SetWriterIndex(byteBuffer.WriterIndex + length);
            return byteBuffer;
        }
        catch
        {
            byteBuffer.SafeRelease();
            throw;
        }
    }

    // A short write would leave stale pooled bytes in the message.
    private static void SerializeExactly<T>(IMessageSerializer<T> serializer, Span<byte> destination, T message) where T : MessageBase
    {
        int written = serializer.Serialize(destination, message);
        if (written != destination.Length)
            ThrowLengthMismatch(serializer, destination.Length, written);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowLengthMismatch(object serializer, int length, int written)
            => throw new InvalidOperationException($"{serializer.GetType().Name} wrote {written} bytes instead of the {length} it reported.");
    }

    // The constructor admits only a serializer of the message type it is registered for, so the cast cannot fail.
    private IMessageSerializer<T> GetSerializer<T>() where T : MessageBase
    {
        if (!_serializers.TryGetValue(typeof(T).TypeHandle, out object serializer))
            ThrowNoSerializerRegistered<T>();

        return (IMessageSerializer<T>)serializer;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNoSerializerRegistered<T>() where T : MessageBase
        => throw new InvalidOperationException($"No serializer registered for {typeof(T).Name}.");
}

public record SerializerInfo(Type MessageType, object Serializer)
{
    public static SerializerInfo Create<T>(IMessageSerializer<T> messageSerializer) where T : MessageBase => new(typeof(T), messageSerializer);
}
