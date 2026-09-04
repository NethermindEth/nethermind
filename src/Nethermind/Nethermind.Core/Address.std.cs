// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

public sealed partial class Address
{
    // The first 16 bytes as a vector, the last 4 as a uint.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static partial bool BytesEqual(ref byte a, ref byte b)
        => Unsafe.As<byte, Vector128<byte>>(ref a) == Unsafe.As<byte, Vector128<byte>>(ref b)
            && Unsafe.As<byte, uint>(ref Unsafe.Add(ref a, Vector128<byte>.Count))
                == Unsafe.As<byte, uint>(ref Unsafe.Add(ref b, Vector128<byte>.Count));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal partial int GetHashCodeNonVirtual() => Bytes.FastHash();
}
