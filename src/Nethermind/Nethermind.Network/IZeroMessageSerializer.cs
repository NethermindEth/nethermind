// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network
{
    public interface IZeroMessageSerializer<T> where T : MessageBase
    {
        void Serialize(Span<byte> buffer, T message);
        T Deserialize(ReadOnlySpan<byte> data, out int consumed);
        int GetLength(T message, out int contentLength);
    }
}
