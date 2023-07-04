// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;

namespace Nethermind.Network
{
    public class NoPad : IMessagePad
    {
        public byte[] Pad(byte[] bytes)
        {
            return bytes;
        }
        public void Pad(IByteBuffer bytes) { }
    }
}
