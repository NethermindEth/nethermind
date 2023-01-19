// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core2.Containers;

namespace Nethermind.Ssz;
public static partial class Ssz
{
    public const int WithdrawalDataLength = sizeof(ulong) + sizeof(ulong) + Address.ByteLength + Ssz.GweiLength;

    public static void Encode(Span<byte> span, Withdrawal container)
    {
        int offset = 0;
        Encode(span, container.Index, ref offset);
        Encode(span, container.ValidatorIndex, ref offset);
        Encode(span, container.Address, ref offset);
        Encode(span, container.AmountInGwei, ref offset);
    }
}
