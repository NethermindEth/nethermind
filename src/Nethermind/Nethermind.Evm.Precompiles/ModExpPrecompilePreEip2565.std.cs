// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class ModExpPrecompilePreEip2565
{
    /// <inheritdoc/>
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.ModExpPrecompile++;

        ReadOnlySpan<byte> span = inputData.Span;
        int baseLength = SafeCast(span.SliceWithZeroPaddingEmptyOnError(0, 32).ToUnsignedBigInteger());
        int expLength = SafeCast(span.SliceWithZeroPaddingEmptyOnError(32, 32).ToUnsignedBigInteger());
        int modulusLength = SafeCast(span.SliceWithZeroPaddingEmptyOnError(64, 32).ToUnsignedBigInteger());

        BigInteger modulusInt = SafeSlice(span, 96L + baseLength + expLength, modulusLength).ToUnsignedBigInteger();

        if (modulusInt.IsZero)
        {
            return new byte[modulusLength];
        }

        BigInteger baseInt = span.SliceWithZeroPaddingEmptyOnError(96, baseLength).ToUnsignedBigInteger();
        BigInteger expInt = SafeSlice(span, 96L + baseLength, expLength).ToUnsignedBigInteger();
        return BigInteger.ModPow(baseInt, expInt, modulusInt).ToBigEndianByteArray(modulusLength);
    }

    private static int SafeCast(BigInteger value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static ReadOnlySpan<byte> SafeSlice(ReadOnlySpan<byte> bytes, long startIndex, int length) =>
        startIndex > int.MaxValue ? default : bytes.SliceWithZeroPaddingEmptyOnError((int)startIndex, length);
}
