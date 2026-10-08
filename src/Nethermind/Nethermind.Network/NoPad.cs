// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network
{
    public class NoPad : IMessagePad
    {
        public int GetPaddingLength() => 0;
        public void Pad(Span<byte> padding) { }
    }
}
