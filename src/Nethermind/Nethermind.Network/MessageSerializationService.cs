// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
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

    public T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase
    {
        if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
            ThrowNoSerializerRegistered<T>();

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
        IZeroInnerMessageSerializer<T>? zeroInnerMessageSerializer = zeroMessageSerializer as IZeroInnerMessageSerializer<T>;
        LengthStack lengths = new(stackalloc int[LengthStack.DefaultStackallocLength]);
        try
        {
            zeroInnerMessageSerializer?.PopulateLength(message, ref lengths);
            int length = zeroInnerMessageSerializer is null ? 64 : lengths.Peek() + p2pMessageLength;

            allocator ??= NethermindBuffers.Default;
            IByteBuffer byteBuffer = allocator.Buffer(length);

            try
            {
                WriteAdaptivePacketType(byteBuffer);
                if (zeroInnerMessageSerializer is null)
                {
                    zeroMessageSerializer.Serialize(byteBuffer, message);
                }
                else
                {
                    zeroInnerMessageSerializer.Serialize(byteBuffer, message, ref lengths);
                }

                return byteBuffer;
            }
            catch (Exception)
            {
                byteBuffer.SafeRelease();
                throw;
            }
        }
        finally
        {
            lengths.Dispose();
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
