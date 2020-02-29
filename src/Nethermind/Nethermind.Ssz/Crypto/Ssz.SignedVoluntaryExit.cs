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
            VoluntaryExit message = DecodeVoluntaryExit(span, ref offset);
            BlsSignature signature = DecodeBlsSignature(span, ref offset);
            return new SignedVoluntaryExit(message, signature);
        }

        public static SignedVoluntaryExit[] DecodeSignedVoluntaryExits(ReadOnlySpan<byte> span)
        {
            if (span.Length % SignedVoluntaryExitLength != 0)
            {
                ThrowInvalidSourceArrayLength<SignedVoluntaryExit>(span.Length, SignedVoluntaryExitLength);
            }

            int count = span.Length / SignedVoluntaryExitLength;
            SignedVoluntaryExit[] containers = new SignedVoluntaryExit[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] =
                    DecodeSignedVoluntaryExit(span.Slice(i * SignedVoluntaryExitLength, SignedVoluntaryExitLength));
            }

            return containers;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, SignedVoluntaryExit container)
        {
            if (span.Length != SignedVoluntaryExitLength)
                ThrowTargetLength<SignedVoluntaryExit>(span.Length, SignedVoluntaryExitLength);
            int offset = 0;
            Encode(span, container.Message, ref offset);
            Encode(span, container.Signature, ref offset);
        }

        public static void Encode(Span<byte> span, SignedVoluntaryExit[] containers)
        {
            if (span.Length != SignedVoluntaryExitLength * containers.Length)
            {
                ThrowTargetLength<SignedVoluntaryExit>(span.Length, SignedVoluntaryExitLength);
            }

            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span.Slice(i * SignedVoluntaryExitLength, SignedVoluntaryExitLength), containers[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SignedVoluntaryExit DecodeSignedVoluntaryExit(ReadOnlySpan<byte> span, ref int offset)
        {
            SignedVoluntaryExit container =
                DecodeSignedVoluntaryExit(span.Slice(offset, SignedVoluntaryExitLength));
            offset += SignedVoluntaryExitLength;
            return container;
        }

        private static void Encode(Span<byte> span, SignedVoluntaryExit[] containers, ref int offset,
            ref int dynamicOffset)
        {
            int length = containers.Length * SignedVoluntaryExitLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length), containers);
            dynamicOffset += length;
            offset += VarOffsetSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, SignedVoluntaryExit value, ref int offset)
        {
            Encode(span.Slice(offset, SignedVoluntaryExitLength), value);
            offset += SignedVoluntaryExitLength;
        }
    }
}