// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network
{
    public interface IMessagePad
    {
        byte[] Pad(byte[] bytes);
        void Pad(IByteBuffer bytes);
    }

}
