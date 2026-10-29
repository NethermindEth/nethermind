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

    public T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase
    {
        if (!_zeroSerializers.TryGetValue(typeof(T).TypeHandle, out object serializerObject))
            ThrowNoSerializerRegistered<T>("serializer");

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
        if (!_zeroSerializers.TryGetValue(typeof(T).TypeHandle, out object serializerObject))
            ThrowNoSerializerRegistered<T>("serializer");

        if (serializerObject is IMessageSerializer<T> serializer)
        {
            T message = serializer.Deserialize(buffer.AsSpan());
            // The serializer does not report how much it read, so the whole message counts as read.
            buffer.SetReaderIndex(buffer.WriterIndex);
            return message;
        }

        if (serializerObject is not IZeroMessageSerializer<T> zeroMessageSerializer)
        {
            ThrowInvalidSerializerType<T>(serializerObject);
            return null!;
        }

        return zeroMessageSerializer.Deserialize(buffer);
    }

    public IByteBuffer ZeroSerialize<T>(T message, IByteBufferAllocator? allocator = null) where T : MessageBase
    {
        if (!_zeroSerializers.TryGetValue(typeof(T).TypeHandle, out object serializerObject))
            ThrowNoSerializerRegistered<T>("serializer");

        if (serializerObject is IMessageSerializer<T> serializer)
            return ZeroSerialize(serializer, message, allocator ?? NethermindBuffers.Default);

        if (serializerObject is not IZeroMessageSerializer<T> zeroMessageSerializer)
        {
            ThrowInvalidSerializerType<T>(serializerObject);
            return null!;
        }

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
    private static void ThrowNoSerializerRegistered<T>(string serializerInterface) where T : MessageBase
        => throw new InvalidOperationException($"No {serializerInterface} registered for {typeof(T).Name}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidSerializerType<T>(object? serializerObject)
        => throw new InvalidOperationException($"Zero serializer for {typeof(T).Name} (registered: {serializerObject?.GetType().Name}) does not implement required interfaces");

}

public record SerializerInfo(Type MessageType, object Serializer)
{
    public static SerializerInfo Create<T>(IZeroMessageSerializer<T> messageSerializer) where T : MessageBase => new(typeof(T), messageSerializer);

    public static SerializerInfo Create<T>(IMessageSerializer<T> messageSerializer) where T : MessageBase => new(typeof(T), messageSerializer);
}
