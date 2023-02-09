// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network
{
    public class NoPad : IMessagePad
    {
        public byte[] Pad(byte[] bytes)
        {
            return bytes;
        }
    }
}
