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
        public const int BlsPublicKeyLength = BlsPublicKey.Length;

        public static void Encode(Span<byte> span, BlsPublicKey value)
        {
            Encode(span, value.Bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, BlsPublicKey value, ref int offset)
        {
            Encode(span.Slice(offset, Ssz.BlsPublicKeyLength), value.Bytes);
            offset += Ssz.BlsPublicKeyLength;
        }

        public static BlsPublicKey DecodeBlsPublicKey(Span<byte> span)
        {
            return new BlsPublicKey(span.ToArray());
        }

        private static BlsPublicKey DecodeBlsPublicKey(ReadOnlySpan<byte> span, ref int offset)
        {
            BlsPublicKey publicKey = new BlsPublicKey(span.Slice(offset, Ssz.BlsPublicKeyLength).ToArray());
            offset += Ssz.BlsPublicKeyLength;
            return publicKey;
        }
    }
}
