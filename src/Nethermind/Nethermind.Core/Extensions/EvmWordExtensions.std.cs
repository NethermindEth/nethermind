// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Core.Extensions;

public static partial class EvmWordExtensions
{
    /// <inheritdoc cref="EvmWordExtensions.ByteSwapScalar" />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial EvmWord ByteSwapScalar(EvmWord word)
    {
        Vector256<ulong> u = word.AsUInt64();
        ulong out0 = Bytes.Bswap64(u.GetElement(3));
        ulong out1 = Bytes.Bswap64(u.GetElement(2));
        ulong out2 = Bytes.Bswap64(u.GetElement(1));
        ulong out3 = Bytes.Bswap64(u.GetElement(0));
        return Vector256.Create(out0, out1, out2, out3).AsByte();
    }
}
