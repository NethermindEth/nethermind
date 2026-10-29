// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network;

public class MessageSerializationService : IMessageSerializationService
{
    private readonly ConcurrentDictionary<RuntimeTypeHandle, object> _zeroSerializers = new();

    public MessageSerializationService(params IReadOnlyList<SerializerInfo> serializers)
    {
        foreach ((Type MessageType, object Serializer) in serializers)
        {
            Type zeroInterface = typeof(IZeroMessageSerializer<>).MakeGenericType(MessageType);
            Type spanInterface = typeof(IMessageSerializer<>).MakeGenericType(MessageType);

            if (!zeroInterface.IsAssignableFrom(Serializer.GetType()) && !spanInterface.IsAssignableFrom(Serializer.GetType()))
            {
                ThrowInvalidSerializer(Serializer, zeroInterface, spanInterface);
            }

            _zeroSerializers[MessageType.TypeHandle] = Serializer;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidSerializer(object serializer, Type zeroInterface, Type spanInterface)
            => throw new ArgumentException($"Serializer of type {serializer.GetType().Name} must implement {zeroInterface.Name} or {spanInterface.Name}.");
    }

    public ArrayPoolSpan<byte> Serialize<T>(T message) where T : MessageBase
    {
        if (!TryGetSerializer(out IMessageSerializer<T> serializer))
            ThrowNoSerializerRegistered<T>(nameof(IMessageSerializer<>));

        int length = serializer.GetLength(message);
        ArrayPoolSpan<byte> buffer = new(length);
        try
        {
            int written = serializer.Serialize(buffer, message);
            if (written != length)
                ThrowLengthMismatch(serializer, length, written);

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        // A short write would leave stale pooled bytes in the message.
        [DoesNotReturn, StackTraceHidden]
        static void ThrowLengthMismatch(object serializer, int length, int written)
            => throw new InvalidOperationException($"{serializer.GetType().Name} wrote {written} bytes instead of the {length} it reported.");
    }

    public T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase
    {
        if (!_zeroSerializers.TryGetValue(typeof(T).TypeHandle, out object serializerObject))
            ThrowNoSerializerRegistered<T>();

        if (serializerObject is IMessageSerializer<T> serializer)
            return serializer.Deserialize(bytes);

        if (serializerObject is not IZeroMessageSerializer<T> zeroMessageSerializer)
        {
            ThrowInvalidSerializerType<T>(serializerObject);
            return null!;
        }

        IByteBuffer byteBuffer = NethermindBuffers.Default.Buffer(bytes.Count);
        byteBuffer.WriteBytes(bytes.Array, bytes.Offset, bytes.Count);
        try
        {
            return zeroMessageSerializer.Deserialize(byteBuffer);
        }
        finally
        {
            byteBuffer.SafeRelease();
        }

    }

    public T Deserialize<T>(IByteBuffer buffer) where T : MessageBase
    {
        if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
            ThrowNoSerializerRegistered<T>();

        return zeroMessageSerializer.Deserialize(buffer);
    }

    public IByteBuffer ZeroSerialize<T>(T message, IByteBufferAllocator? allocator = null) where T : MessageBase
    {
        if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
            ThrowNoSerializerRegistered<T>();

        void WriteAdaptivePacketType(in IByteBuffer buffer)
        {
            if (message is P2PMessage p2PMessage)
            {
                buffer.WriteBytes(Rlp.Encode(p2PMessage.AdaptivePacketType, stackalloc byte[sizeof(long) + 1]));
            }
        }

        int p2pMessageLength = (message is P2PMessage ? sizeof(int) : 0);
        int length = zeroMessageSerializer is IZeroInnerMessageSerializer<T> zeroInnerMessageSerializer
            ? zeroInnerMessageSerializer.GetLength(message, out _) + p2pMessageLength
            : 64;

        allocator ??= NethermindBuffers.Default;
        IByteBuffer byteBuffer = allocator.Buffer(length);

        try
        {
            WriteAdaptivePacketType(byteBuffer);
            zeroMessageSerializer.Serialize(byteBuffer, message);
            return byteBuffer;
        }
        catch (Exception)
        {
            byteBuffer.SafeRelease();
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

    private bool TryGetSerializer<T>(out IMessageSerializer<T> serializer) where T : MessageBase
    {
        if (_zeroSerializers.TryGetValue(typeof(T).TypeHandle, out object serializerObject) && serializerObject is IMessageSerializer<T> messageSerializer)
        {
            serializer = messageSerializer;
            return true;
        }

        serializer = null!;
        return false;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNoSerializerRegistered<T>(string serializerInterface = nameof(IZeroMessageSerializer<>)) where T : MessageBase
        => throw new InvalidOperationException($"No {serializerInterface} registered for {typeof(T).Name}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidSerializerType<T>(object? serializerObject)
        => throw new InvalidOperationException($"Zero serializer for {nameof(T)} (registered: {serializerObject?.GetType().Name}) does not implement required interfaces");

}

public record SerializerInfo(Type MessageType, object Serializer)
{
    public static SerializerInfo Create<T>(IZeroMessageSerializer<T> messageSerializer) where T : MessageBase => new(typeof(T), messageSerializer);

    public static SerializerInfo Create<T>(IMessageSerializer<T> messageSerializer) where T : MessageBase => new(typeof(T), messageSerializer);
}
