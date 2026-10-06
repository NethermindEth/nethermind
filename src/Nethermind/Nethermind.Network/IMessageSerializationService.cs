// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;

namespace Nethermind.Network
{
    public interface IMessageSerializationService
    {
        PooledBuffer ZeroSerialize<T>(T message) where T : MessageBase;
        T Deserialize<T>(ReadOnlySpan<byte> data) where T : MessageBase;
        T Deserialize<T>(ReadOnlySpan<byte> data, out int consumed) where T : MessageBase;
    }
}
