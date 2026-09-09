// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core;

public sealed partial class Address
{
    // Two ulongs and a uint: the vector compare has no hardware behind it here.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static partial bool BytesEqual(ref byte a, ref byte b)
        => Unsafe.ReadUnaligned<ulong>(ref a) == Unsafe.ReadUnaligned<ulong>(ref b)
            && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 8)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))
            && Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref a, 16)) == Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16));
}
