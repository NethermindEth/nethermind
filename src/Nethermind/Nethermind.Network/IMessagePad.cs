// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network
{
    public interface IMessagePad
    {
        int GetPaddingLength();
        void Pad(Span<byte> padding);
    }

}
