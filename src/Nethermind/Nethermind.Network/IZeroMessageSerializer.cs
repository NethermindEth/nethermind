// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network
{
    public interface IZeroMessageSerializer<T> where T : MessageBase
    {
        void Serialize(IByteBuffer byteBuffer, T message);
        T Deserialize(IByteBuffer byteBuffer);
    }
}
