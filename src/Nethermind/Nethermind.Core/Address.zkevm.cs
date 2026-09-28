// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Arm = System.Runtime.Intrinsics.Arm;
using x64 = System.Runtime.Intrinsics.X86;

namespace Nethermind.Core;

public sealed partial class Address
{
    // One account access probes several maps with the same instance, and every storage cell of the address hashes
    // its words again. An address is immutable and the guest seeds its hashes once, before hashing anything, so both
    // can be kept; 0 means not computed yet.
    private ulong _wordSum;
    private int _hashCode;

    // ValueAddress and AddressAsKey hash with FastHash64For20Bytes, which takes AES where it can. The kept sums are its
    // scalar path, the guest's, so a host with AES hashes the shared way to agree with them.
    private static bool HashesWithAes => x64.Aes.IsSupported || Arm.Aes.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal partial int GetHashCodeNonVirtual()
    {
        if (HashesWithAes) return unchecked((int)GetHashCode64());

        int hashCode = _hashCode;
        if (hashCode == 0) _hashCode = hashCode = unchecked((int)SpanExtensions.FinalizeAddressSum(WordSum()));
        return hashCode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal partial long GetHashCode64(in UInt256 index)
    {
        ref byte indexBytes = ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in index));
        return HashesWithAes
            ? SpanExtensions.FastHash64ForAddressAndSlot(ref Unsafe.AsRef(in FirstByte), ref indexBytes)
            : (long)SpanExtensions.MixSlot(WordSum(), ref indexBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong WordSum()
    {
        ulong wordSum = _wordSum;
        if (wordSum == 0) _wordSum = wordSum = SpanExtensions.SumAddressWords(ref Unsafe.AsRef(in FirstByte));
        return wordSum;
    }

    // Two ulongs and a uint: the vector compare has no hardware behind it here.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static partial bool BytesEqual(ref byte a, ref byte b)
        => Unsafe.ReadUnaligned<ulong>(ref a) == Unsafe.ReadUnaligned<ulong>(ref b)
            && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, 8)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))
            && Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref a, 16)) == Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16));
}
