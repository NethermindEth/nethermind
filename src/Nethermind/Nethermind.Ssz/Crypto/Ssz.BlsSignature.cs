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
using Nethermind.Core2;
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static void Encode(Span<byte> span, BlsSignature value)
        {
            Encode(span, value.Bytes);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BlsSignature DecodeBlsSignature(Span<byte> span, ref int offset)
        {
            BlsSignature blsSignature = DecodeBlsSignature(span.Slice(offset, ByteLength.BlsSignatureLength));
            offset += ByteLength.BlsSignatureLength;
            return blsSignature;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, BlsSignature value, ref int offset)
        {
            Encode(span.Slice(offset, ByteLength.BlsSignatureLength), value.Bytes);
            offset += ByteLength.BlsSignatureLength;
        }
        
        public static BlsSignature DecodeBlsSignature(Span<byte> span)
        {
            return new BlsSignature(DecodeBytes(span).ToArray());
        }    
    }
}