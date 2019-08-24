/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Reflection;
using DotNetty.Buffers;
using Nethermind.Core;

namespace Nethermind.Network
{
    public class MessageSerializationService : IMessageSerializationService
    {
        private readonly ConcurrentDictionary<RuntimeTypeHandle, object> _serializers = new ConcurrentDictionary<RuntimeTypeHandle, object>();

        public T Deserialize<T>(byte[] bytes) where T : MessageBase
        {
            IMessageSerializer<T> serializer = GetSerializer<T>();
            return serializer.Deserialize(bytes);
        }

        public T Deserialize<T>(IByteBuffer buffer) where T : MessageBase
        {
            IMessageSerializer<T> serializer = GetSerializer<T>();
            IZeroMessageSerializer<T> zeroSerializer = serializer as IZeroMessageSerializer<T>;
            if (zeroSerializer != null)
            {
                return zeroSerializer.Deserialize(buffer);
            }

            return serializer.Deserialize(buffer.ReadAllBytes());
        }

        public void Register(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsClass)
                {
                    continue;
                }

                var implementedInterfaces = type.GetInterfaces();
                foreach (var implementedInterface in implementedInterfaces)
                {
                    if (!implementedInterface.IsGenericType)
                    {
                        continue;
                    }

                    var interfaceGenericDefinition = implementedInterface.GetGenericTypeDefinition();
                    if (interfaceGenericDefinition == typeof(IMessageSerializer<>).GetGenericTypeDefinition())
                    {
                        var constructor = type.GetConstructor(Type.EmptyTypes);
                        if (constructor == null)
                        {
                            continue;
                        }

                        _serializers[implementedInterface.GenericTypeArguments[0].TypeHandle] = Activator.CreateInstance(type);
                    }
                }
            }
        }

        public void Register<T>(IMessageSerializer<T> messageSerializer) where T : MessageBase
        {
            _serializers[typeof(T).TypeHandle] = messageSerializer;
        }

        [Todo(Improve.Performance, "WIP - will add a zero serializers here")]
        public void Serialize<T>(T message, IByteBuffer byteBuffer) where T : MessageBase
        {
            IMessageSerializer<T> serializer = GetSerializer<T>();
            IZeroMessageSerializer<T> zeroSerializer = serializer as IZeroMessageSerializer<T>;
            
            if (zeroSerializer != null)
            {
                zeroSerializer.Serialize(byteBuffer, message);
            }
            else
            {
                byte[] serialized = serializer.Serialize(message);
                byteBuffer.EnsureWritable(serialized.Length, true);
                byteBuffer.WriteBytes(serialized);
            }
        }

        public byte[] Serialize<T>(T messageBase) where T : MessageBase
        {
            IMessageSerializer<T> serializer = GetSerializer<T>();
            return serializer.Serialize(messageBase);
        }

        private IMessageSerializer<T> GetSerializer<T>() where T : MessageBase
        {
            RuntimeTypeHandle typeHandle = typeof(T).TypeHandle;
            if (!_serializers.TryGetValue(typeHandle, out object serializerObject))
            {
                Type type = typeof(T);
                throw new InvalidOperationException($"No {nameof(IMessageSerializer<T>)} registered for {type.Name}.");
            }

            if (!(serializerObject is IMessageSerializer<T> serializer))
            {
                throw new InvalidOperationException($"Missing matching serializer for {nameof(T)} (registered: {serializerObject?.GetType()?.Name})");
            }

            return serializer;
        }
    }
}