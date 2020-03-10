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
        public const int SignedBeaconBlockHeaderLength = BeaconBlockHeaderLength + BlsSignatureLength;

        public static SignedBeaconBlockHeader DecodeSignedBeaconBlockHeader(ReadOnlySpan<byte> span)
        {
            if (span.Length != SignedBeaconBlockHeaderLength)
                ThrowSourceLength<SignedBeaconBlockHeader>(span.Length, SignedBeaconBlockHeaderLength);
            int offset = 0;
            return DecodeSignedBeaconBlockHeader(span, ref offset);
        }

        public static void Encode(Span<byte> span, SignedBeaconBlockHeader container)
        {
            if (span.Length != SignedBeaconBlockHeaderLength)
                ThrowTargetLength<SignedBeaconBlockHeader>(span.Length, SignedBeaconBlockHeaderLength);
            int offset = 0;
            Encode(span, container, ref offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SignedBeaconBlockHeader DecodeSignedBeaconBlockHeader(ReadOnlySpan<byte> span, ref int offset)
        {
            BeaconBlockHeader message = DecodeBeaconBlockHeader(span, ref offset);
            BlsSignature signature = DecodeBlsSignature(span, ref offset);
            return new SignedBeaconBlockHeader(message, signature);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, SignedBeaconBlockHeader container, ref int offset)
        {
            Encode(span, container.Message, ref offset);
            Encode(span, container.Signature, ref offset);
        }
    }
}