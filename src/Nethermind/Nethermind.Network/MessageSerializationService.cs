// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Reflection;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network
{
    public class MessageSerializationService : IMessageSerializationService
    {
        private readonly ConcurrentDictionary<RuntimeTypeHandle, object> _zeroSerializers = new ConcurrentDictionary<RuntimeTypeHandle, object>();

        public T Deserialize<T>(byte[] bytes) where T : MessageBase
        {
            if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
                throw new InvalidOperationException($"No {nameof(IZeroMessageSerializer<T>)} registered for {typeof(T).Name}.");

            IByteBuffer byteBuffer = PooledByteBufferAllocator.Default.Buffer(bytes.Length);
            byteBuffer.WriteBytes(bytes);
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
                throw new InvalidOperationException($"No {nameof(IZeroMessageSerializer<T>)} registered for {typeof(T).Name}.");

            return zeroMessageSerializer.Deserialize(buffer);
        }

        public void Register(Assembly assembly)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                if (!type.IsClass)
                {
                    continue;
                }

                Type[] implementedInterfaces = type.GetInterfaces();
                foreach (Type implementedInterface in implementedInterfaces)
                {
                    if (!implementedInterface.IsGenericType)
                    {
                        continue;
                    }

                    Type interfaceGenericDefinition = implementedInterface.GetGenericTypeDefinition();

                    if (interfaceGenericDefinition == typeof(IZeroMessageSerializer<>).GetGenericTypeDefinition())
                    {
                        ConstructorInfo constructor = type.GetConstructor(Type.EmptyTypes);
                        if (constructor is null)
                        {
                            continue;
                        }

                        _zeroSerializers[implementedInterface.GenericTypeArguments[0].TypeHandle] = Activator.CreateInstance(type);
                    }
                }
            }
        }

        public void Register<T>(IZeroMessageSerializer<T> messageSerializer) where T : MessageBase
        {
            _zeroSerializers[typeof(T).TypeHandle] = messageSerializer;
        }

        public IByteBuffer ZeroSerialize<T>(T message, AbstractByteBufferAllocator? allocator = null) where T : MessageBase
        {
            if (!TryGetZeroSerializer(out IZeroMessageSerializer<T> zeroMessageSerializer))
                throw new InvalidOperationException($"No {nameof(IZeroMessageSerializer<T>)} registered for {typeof(T).Name}.");

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

            allocator ??= PooledByteBufferAllocator.Default;
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

            throw new InvalidOperationException($"Zero serializer for {nameof(T)} (registered: {serializerObject?.GetType().Name}) does not implement required interfaces");
        }

    }
}
