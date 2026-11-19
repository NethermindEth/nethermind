// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Zkvm.Abstractions;
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
    internal static ulong ReadIndexBits(ref byte keccak) => ZkEvmBitOperations.ReadUInt64BigEndian(ref keccak);

    public static Bloom ZeroLength => field ??= CreateZeroLength();

    /// <remarks>Airbender: libairbender hashes an address or a topic and sets its bits, keeping the bits of the
    /// inputs a block's logs repeat.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private partial bool TrySetNatively(ReadOnlySpan<byte> sequence, Bloom? masterBloom)
    {
        if (!AirbenderFlag.IsActive || (sequence.Length != Address.Size && sequence.Length != Hash256.Size)) return false;
        SetNatively(sequence, masterBloom);
        return true;
    }

    private unsafe void SetNatively(ReadOnlySpan<byte> sequence, Bloom? masterBloom)
    {
        int length = sequence.Length;
        // libairbender v1.0.7 loads eight words even for a 20-byte address.
        Span<uint> padded = stackalloc uint[Hash256.Size / sizeof(uint)];
        scoped ReadOnlySpan<byte> input = sequence;
        if (length == Address.Size)
        {
            sequence.CopyTo(MemoryMarshal.AsBytes(padded));
            input = MemoryMarshal.AsBytes(padded);
        }

        fixed (byte* source = input)
        fixed (byte* bloom = _bloomData.AsSpan())
        fixed (byte* master = masterBloom is null ? default : masterBloom._bloomData.AsSpan())
            Accelerators.AirbenderBloomSet(bloom, master, source, (uint)length);
    }
}
