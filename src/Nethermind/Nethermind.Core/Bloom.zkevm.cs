// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

public partial class Bloom
{
    /// <summary>The first eight bytes of <paramref name="keccak"/>, big-endian; the bloom indexes are its top 48 bits.</summary>
    /// <remarks>
    /// Without Zbb, gathering the bytes most significant first takes fewer instructions than reversing a loaded word.
    /// See <c>Bloom.std.cs</c> for the host form.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadIndexBits(ref byte keccak) => ZkEvmBitOperations.ReadUInt64BigEndian(ref keccak);

    public static Bloom ZeroLength => field ??= CreateZeroLength();
}
