// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using DotNetty.Buffers;

namespace Nethermind.Network
{
    public interface IMessageSerializationService
    {
        IByteBuffer ZeroSerialize<T>(T message) where T : MessageBase;
        byte[] Serialize<T>(T messageBase) where T : MessageBase;
        T Deserialize<T>(byte[] bytes) where T : MessageBase;
        T Deserialize<T>(IByteBuffer buffer) where T : MessageBase;
        void Register(Assembly assembly);
        void Register<T>(IMessageSerializer<T> messageSerializer) where T : MessageBase;
        void Register<T>(IZeroMessageSerializer<T> messageSerializer) where T : MessageBase;
    }
}
