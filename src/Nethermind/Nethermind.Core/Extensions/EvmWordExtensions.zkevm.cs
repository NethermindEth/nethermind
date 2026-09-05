// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Core.Extensions;

public static partial class EvmWordExtensions
{
    /// <inheritdoc cref="EvmWordExtensions.ByteSwapScalar" />
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial EvmWord ByteSwapScalar(EvmWord word)
    {
        // Straight over the four lanes: the parameter is already this frame's copy, and the result is
        // written lane by lane, so neither side needs the staging buffer a Vector256 would go through.
        ref ulong src = ref Unsafe.As<EvmWord, ulong>(ref word);
        Unsafe.SkipInit(out EvmWord result);
        ref ulong dst = ref Unsafe.As<EvmWord, ulong>(ref result);

        dst = Bytes.Bswap64(Unsafe.Add(ref src, 3));
        Unsafe.Add(ref dst, 1) = Bytes.Bswap64(Unsafe.Add(ref src, 2));
        Unsafe.Add(ref dst, 2) = Bytes.Bswap64(Unsafe.Add(ref src, 1));
        Unsafe.Add(ref dst, 3) = Bytes.Bswap64(src);

        return result;
    }
}
