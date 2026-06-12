// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

public sealed partial class Address
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool Equals20Bytes(ref byte a, ref byte b) =>
        // First 16 bytes via Vector128, last 4 bytes via uint.
        Unsafe.As<byte, Vector128<byte>>(ref a) ==
        Unsafe.As<byte, Vector128<byte>>(ref b) &&
        Unsafe.As<byte, uint>(ref Unsafe.Add(ref a, Vector128<byte>.Count)) ==
        Unsafe.As<byte, uint>(ref Unsafe.Add(ref b, Vector128<byte>.Count));

    public override partial int GetHashCode() => Bytes.FastHash();
}
