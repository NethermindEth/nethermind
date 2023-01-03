// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core2;
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int BlsSignatureLength = BlsSignature.Length;

        public static void Encode(Span<byte> span, BlsSignature value)
        {
            Encode(span, value.Bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BlsSignature DecodeBlsSignature(ReadOnlySpan<byte> span, ref int offset)
        {
            BlsSignature blsSignature = DecodeBlsSignature(span.Slice(offset, Ssz.BlsSignatureLength));
            offset += Ssz.BlsSignatureLength;
            return blsSignature;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, BlsSignature value, ref int offset)
        {
            Encode(span.Slice(offset, Ssz.BlsSignatureLength), value.Bytes);
            offset += Ssz.BlsSignatureLength;
        }

        public static BlsSignature DecodeBlsSignature(ReadOnlySpan<byte> span)
        {
            return new BlsSignature(span.ToArray());
        }
    }
}
