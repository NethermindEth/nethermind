// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.TxPool;

[InlineArray(Length)]
internal struct Hash96 : IEquatable<Hash96>, IHash64bit<Hash96>
{
    public const int Length = 3;

    private int _element0;

    public static Hash96 From(Hash256 hash)
    {
        Hash96 hash96 = default;

        ref byte source = ref MemoryMarshal.GetReference(hash.Bytes);
        ref int target = ref hash96._element0;
        target = Unsafe.ReadUnaligned<int>(ref source);
        Unsafe.Add(ref target, 1) = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref source, sizeof(int)));
        Unsafe.Add(ref target, 2) = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref source, 2 * sizeof(int)));

        return hash96;
    }

    public readonly bool Equals(Hash96 other) => Equals(in other);

    public readonly bool Equals(in Hash96 other)
    {
        ref int left = ref Unsafe.AsRef(in _element0);
        ref int right = ref Unsafe.AsRef(in other._element0);

        return left == right
            && Unsafe.Add(ref left, 1) == Unsafe.Add(ref right, 1)
            && Unsafe.Add(ref left, 2) == Unsafe.Add(ref right, 2);
    }

    public override readonly bool Equals(object? obj) => obj is Hash96 other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override readonly int GetHashCode() => _element0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long GetHashCode64()
    {
        ref int first = ref Unsafe.AsRef(in _element0);
        return Unsafe.ReadUnaligned<long>(ref Unsafe.As<int, byte>(ref first));
    }
}
