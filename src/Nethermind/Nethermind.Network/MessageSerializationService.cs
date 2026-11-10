// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Network.Rlpx;

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

    public T Deserialize<T>(ZeroPacket packet) where T : MessageBase
    {
        IMessageSerializer<T> serializer = GetSerializer<T>();
        Memory<byte> content = packet.Content;
        return serializer is IOwningMessageSerializer<T> owningSerializer
            ? owningSerializer.Deserialize(packet.LeaseContent(), content)
            : serializer.Deserialize(content.Span);
    }

    public int GetLength<T>(T message) where T : MessageBase => GetSerializer<T>().GetLength(message);

    public void Serialize<T>(T message, Span<byte> destination) where T : MessageBase
        => SerializeExactly(GetSerializer<T>(), destination, message);

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
