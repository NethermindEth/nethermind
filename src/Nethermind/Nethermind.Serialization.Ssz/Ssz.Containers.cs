// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

public partial class Ssz
{
    private const int VarOffsetSize = sizeof(uint);

    private static void DecodeDynamicOffset(ReadOnlySpan<byte> span, ref int offset, out int dynamicOffset)
    {
        dynamicOffset = (int)DecodeUInt(span.Slice(offset, VarOffsetSize));
        offset += sizeof(uint);
    }
}
