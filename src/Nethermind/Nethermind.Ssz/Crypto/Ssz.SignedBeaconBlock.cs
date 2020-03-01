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
        private const int SignedBeaconBlockDynamicOffset = VarOffsetSize + BlsSignatureLength;

        public static int SignedBeaconBlockLength(SignedBeaconBlock container)
        {
            return SignedBeaconBlockDynamicOffset + Ssz.BeaconBlockLength(container.Message);
        }

        public static SignedBeaconBlock DecodeSignedBeaconBlock(ReadOnlySpan<byte> span)
        {
            int offset = 0;
            return DecodeSignedBeaconBlock(span, ref offset);
        }

        public static void Encode(Span<byte> span, SignedBeaconBlock container)
        {
            int expectedLength = SignedBeaconBlockLength(container);
            if (span.Length != expectedLength)
                ThrowTargetLength<SignedBeaconBlock>(span.Length, expectedLength);
            int offset = 0;
            Encode(span, container, ref offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SignedBeaconBlock DecodeSignedBeaconBlock(ReadOnlySpan<byte> span, ref int offset)
        {
            BeaconBlock message = DecodeBeaconBlock(span, ref offset);
            BlsSignature signature = DecodeBlsSignature(span, ref offset);
            return new SignedBeaconBlock(message, signature);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, SignedBeaconBlock container, ref int offset)
        {
            Encode(span, container.Message, ref offset);
            Encode(span, container.Signature, ref offset);
        }
    }
}