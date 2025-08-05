// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;

namespace Nethermind.Network
{
    public interface IMessageSerializationService
    {
        IByteBuffer ZeroSerialize<T>(T message, IByteBufferAllocator? allocator = null) where T : MessageBase;
        T Deserialize<T>(ArraySegment<byte> bytes) where T : MessageBase;
        T Deserialize<T>(IByteBuffer buffer) where T : MessageBase;
    }
}
