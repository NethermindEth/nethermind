// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int SignedVoluntaryExitLength = VoluntaryExitLength + BlsSignatureLength;

        public static SignedVoluntaryExit DecodeSignedVoluntaryExit(ReadOnlySpan<byte> span)
        {
            if (span.Length != SignedVoluntaryExitLength)
                ThrowSourceLength<SignedVoluntaryExit>(span.Length, SignedVoluntaryExitLength);
            int offset = 0;
            return DecodeSignedVoluntaryExit(span, ref offset);
        }

        public static SignedVoluntaryExit[] DecodeSignedVoluntaryExitList(ReadOnlySpan<byte> span)
        {
            if ((span.Length - VarOffsetSize) % SignedVoluntaryExitLength != 0)
            {
                ThrowInvalidSourceArrayLength<SignedVoluntaryExit>(span.Length, SignedVoluntaryExitLength);
            }

            int offset = 0;
            DecodeDynamicOffset(span, ref offset, out int _);
            return DecodeSignedVoluntaryExitVector(span, ref offset, span.Length);
        }

        public static void Encode(Span<byte> span, SignedVoluntaryExit container)
        {
            if (span.Length != SignedVoluntaryExitLength)
                ThrowTargetLength<SignedVoluntaryExit>(span.Length, SignedVoluntaryExitLength);
            int offset = 0;
            Encode(span, container, ref offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SignedVoluntaryExit DecodeSignedVoluntaryExit(ReadOnlySpan<byte> span, ref int offset)
        {
            VoluntaryExit message = DecodeVoluntaryExit(span, ref offset);
            BlsSignature signature = DecodeBlsSignature(span, ref offset);
            return new SignedVoluntaryExit(message, signature);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SignedVoluntaryExit[] DecodeSignedVoluntaryExitVector(ReadOnlySpan<byte> span, ref int offset, int endingOffset)
        {
            int count = (endingOffset - offset) / SignedVoluntaryExitLength;
            SignedVoluntaryExit[] containers = new SignedVoluntaryExit[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeSignedVoluntaryExit(span, ref offset);
            }
            return containers;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, SignedVoluntaryExit container, ref int offset)
        {
            Encode(span, container.Message, ref offset);
            Encode(span, container.Signature, ref offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EncodeList(Span<byte> span, IReadOnlyList<SignedVoluntaryExit> containers, ref int offset)
        {
            // fixed parts
            foreach (SignedVoluntaryExit signedVoluntaryExit in containers)
            {
                Encode(span, signedVoluntaryExit, ref offset);
            }

            // all items are fixed length, so no variable parts
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EncodeVector(Span<byte> span, SignedVoluntaryExit[] containers, ref int offset)
        {
            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span, containers[i], ref offset);
            }
        }
    }
}
