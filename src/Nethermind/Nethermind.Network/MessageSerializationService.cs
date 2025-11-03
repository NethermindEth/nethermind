// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    public class MessageSerializationService : IMessageSerializationService
    {
        private readonly ConcurrentDictionary<RuntimeTypeHandle, object> _zeroSerializers = new();
        private readonly ILogger _logger;

        public MessageSerializationService(ILogManager logManager, params IReadOnlyList<SerializerInfo> serializers)
        {
            _logger = logManager.GetClassLogger<MessageSerializationService>();
            Type openGeneric = typeof(IZeroMessageSerializer<>);

            foreach ((Type messageType, object serializer) in serializers)
            {
                Type expectedInterface = openGeneric.MakeGenericType(messageType);

                if (!expectedInterface.IsAssignableFrom(serializer.GetType()))
                    ThrowMissingInterface(serializer, expectedInterface);

                _zeroSerializers.TryAdd(messageType.TypeHandle, serializer);
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowMissingInterface(object serializer, Type expectedInterface)
            {
                throw new ArgumentException($"Serializer of type {serializer.GetType().Name} must implement {expectedInterface.Name}.");
            }
        }

        public T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase
        {
            if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
                ThrowMissingSerializerException<T>();

            IByteBuffer byteBuffer = NethermindBuffers.Default.Buffer(bytes.Count);
            byteBuffer.WriteBytes(bytes.Array, bytes.Offset, bytes.Count);
            try
            {
                return zeroMessageSerializer.Deserialize(byteBuffer);
            }
            catch (RlpLimitException)
            {
                if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR RLP limit exception while deserializing message {bytes.AsSpan().ToHexString()}.");
                throw;
            }
            finally
            {
                byteBuffer.SafeRelease();
            }

        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowMissingSerializerException<T>() where T : MessageBase
        {
            throw new InvalidOperationException($"No {nameof(IZeroMessageSerializer<T>)} registered for {typeof(T).Name}.");
        }

        public T Deserialize<T>(IByteBuffer buffer) where T : MessageBase
        {
            if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
                ThrowMissingSerializerException<T>();

            try
            {
                return zeroMessageSerializer.Deserialize(buffer);
            }
            catch (RlpLimitException)
            {
                if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR RLP limit exception while deserializing message {buffer.AsSpan().ToHexString()}.");
                throw;
            }
        }

        public IByteBuffer ZeroSerialize<T>(T message, IByteBufferAllocator? allocator = null) where T : MessageBase
        {
            if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
                ThrowMissingSerializerException<T>();

            void WriteAdaptivePacketType(in IByteBuffer buffer)
            {
                if (message is P2PMessage p2PMessage)
                {
                    buffer.WriteByte(p2PMessage.AdaptivePacketType);
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

            ThrowInterfaceMissing(serializerObject);
            return false;

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowInterfaceMissing(object serializerObject)
            {
                throw new InvalidOperationException($"Zero serializer for {nameof(T)} (registered: {serializerObject?.GetType().Name}) does not implement required interfaces");
            }
        }
    }

    public record SerializerInfo(Type MessageType, object Serializer)
    {
        public static SerializerInfo Create<T>(IZeroMessageSerializer<T> messageSerializer) where T : MessageBase => new SerializerInfo(typeof(T), messageSerializer);
    }
}
