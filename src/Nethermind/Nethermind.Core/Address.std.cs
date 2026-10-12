// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

public sealed partial class Address
{
    private int _hashCode;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal partial int GetHashCodeNonVirtual()
    {
        int hashCode = Volatile.Read(ref _hashCode);
        if (hashCode == 0)
        {
            hashCode = unchecked((int)GetHashCode64());
            Volatile.Write(ref _hashCode, hashCode);
        }

        return hashCode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal partial long GetHashCode64(in UInt256 index) => SpanExtensions.FastHash64ForAddressAndSlot(
        ref Unsafe.AsRef(in FirstByte), ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in index)));

    // The first 16 bytes as a vector, the last 4 as a uint.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static partial bool BytesEqual(ref byte a, ref byte b)
        => Unsafe.As<byte, Vector128<byte>>(ref a) == Unsafe.As<byte, Vector128<byte>>(ref b)
            && Unsafe.As<byte, uint>(ref Unsafe.Add(ref a, Vector128<byte>.Count))
                == Unsafe.As<byte, uint>(ref Unsafe.Add(ref b, Vector128<byte>.Count));
}
