// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Optimism.CL.Decoders;

public static class SolidityAbiDecoder
{
    public static UInt64 ReadUInt64(ReadOnlySpan<byte> source)
    {
        var padding = source.TakeAndMove(24);
        if (!padding.IsZero()) throw new FormatException("Number padding was not empty");

        return BinaryPrimitives.ReadUInt64BigEndian(source);
    }

    public static Address ReadAddress(ReadOnlySpan<byte> source)
    {
        var padding = source.TakeAndMove(12);
        if (!padding.IsZero()) throw new FormatException("Address padding was not empty");

        return new Address(source.ToArray());
    }
}
