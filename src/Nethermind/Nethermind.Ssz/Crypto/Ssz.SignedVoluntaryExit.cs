//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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