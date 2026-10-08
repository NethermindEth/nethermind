// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network;

public class MessageSerializationService : IMessageSerializationService
{
    private readonly ConcurrentDictionary<RuntimeTypeHandle, object> _zeroSerializers = new();

    public MessageSerializationService(params IReadOnlyList<SerializerInfo> serializers)
    {
        Type openGeneric = typeof(IZeroMessageSerializer<>);

        foreach ((Type MessageType, object Serializer) in serializers)
        {
            Type expectedInterface = openGeneric.MakeGenericType(MessageType);

            if (!expectedInterface.IsAssignableFrom(Serializer.GetType()))
            {
                ThrowInvalidSerializer(Serializer, expectedInterface);
            }

            _zeroSerializers[MessageType.TypeHandle] = Serializer;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidSerializer(object serializer, Type expectedInterface)
            => throw new ArgumentException($"Serializer of type {serializer.GetType().Name} must implement {expectedInterface.Name}.");
    }

    public T Deserialize<T>(ReadOnlySpan<byte> data) where T : MessageBase =>
        Deserialize<T>(data, out _);

    public T Deserialize<T>(ReadOnlySpan<byte> data, out int consumed) where T : MessageBase
    {
        if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
            ThrowNoSerializerRegistered<T>();

        return zeroMessageSerializer.Deserialize(data, out consumed);

    }

    public PooledBuffer ZeroSerialize<T>(T message) where T : MessageBase
    {
        if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
            ThrowNoSerializerRegistered<T>();

        P2PMessage? p2PMessage = message as P2PMessage;
        int prefixLength = p2PMessage is null ? 0 : Rlp.LengthOf(p2PMessage.AdaptivePacketType);
        PooledBuffer buffer = PooledBuffer.Rent(zeroMessageSerializer.GetLength(message, out _) + prefixLength);

        try
        {
            Span<byte> span = buffer.Span;
            if (p2PMessage is not null)
            {
                Span<byte> prefix = Rlp.Encode(p2PMessage.AdaptivePacketType, span);
                if (prefix.Length != prefixLength)
                {
                    ThrowPrefixLengthMismatch(prefix.Length, prefixLength);
                }
            }

            zeroMessageSerializer.Serialize(span.Slice(prefixLength), message);
            return buffer;
        }
        catch (Exception)
        {
            buffer.Dispose();
            throw;
        }
    }

    private bool TryGetZeroSerializer<T>(out IZeroMessageSerializer<T> serializer) where T : MessageBase
    {
        RuntimeTypeHandle typeHandle = typeof(T).TypeHandle;
        if (!_zeroSerializers.TryGetValue(typeHandle, out object serializerObject))
        {
            serializer = null!;
            return false;
        }

        if (serializerObject is IZeroMessageSerializer<T> messageSerializer)
        {
            serializer = messageSerializer;
            return true;
        }

        ThrowInvalidSerializerType<T>(serializerObject);
        // unreachable
        serializer = null!;
        return false;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowPrefixLengthMismatch(int actual, int expected)
        => throw new InvalidOperationException($"Encoded adaptive packet type length {actual} differs from measured {expected}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNoSerializerRegistered<T>() where T : MessageBase
        => throw new InvalidOperationException($"No {nameof(IZeroMessageSerializer<>)} registered for {typeof(T).Name}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidSerializerType<T>(object? serializerObject)
        => throw new InvalidOperationException($"Zero serializer for {nameof(T)} (registered: {serializerObject?.GetType().Name}) does not implement required interfaces");

}

public record SerializerInfo(Type MessageType, object Serializer)
{
    public static SerializerInfo Create<T>(IZeroMessageSerializer<T> messageSerializer) where T : MessageBase => new(typeof(T), messageSerializer);
}
