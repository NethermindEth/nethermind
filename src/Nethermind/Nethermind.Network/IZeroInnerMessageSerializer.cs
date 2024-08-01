// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network
{
    public interface IZeroInnerMessageSerializer<T> : IZeroMessageSerializer<T> where T : MessageBase
    {
        int GetLength(T message, out int contentLength);
    }
}
