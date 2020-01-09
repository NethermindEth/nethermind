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
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static void Encode(Span<byte> span, Deposit[]? containers)
        {
            if (span.Length != ByteLength.DepositLength * (containers?.Length ?? 0))
            {
                ThrowTargetLength<Deposit>(span.Length, ByteLength.DepositLength);
            }

            if (!(containers is null))
            {
                for (int i = 0; i < containers.Length; i++)
                {
                    Encode(span.Slice(i * ByteLength.DepositLength, ByteLength.DepositLength), containers[i]);
                }
            }
        }

        public static Deposit[] DecodeDeposits(Span<byte> span)
        {
            if (span.Length % ByteLength.DepositLength != 0)
            {
                ThrowInvalidSourceArrayLength<Deposit>(span.Length, ByteLength.DepositLength);
            }

            int count = span.Length / ByteLength.DepositLength;
            Deposit[] containers = new Deposit[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeDeposit(span.Slice(i * ByteLength.DepositLength, ByteLength.DepositLength));
            }

            return containers;
        }
        
        private static void Encode(Span<byte> span, Deposit[]? containers, ref int offset, ref int dynamicOffset)
        {
            int length = (containers?.Length ?? 0) * ByteLength.DepositLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length), containers);
            dynamicOffset += length;
            offset += VarOffsetSize;
        }
        
        public static void Encode(Span<byte> span, Deposit? container)
        {
            if (span.Length != ByteLength.DepositLength) ThrowTargetLength<Deposit>(span.Length, ByteLength.DepositLength);
            if (container == null) return;
            Encode(span.Slice(0, ByteLength.DepositLengthOfProof), container.Proof);
            Encode(span.Slice(ByteLength.DepositLengthOfProof), container.Data);
        }

        private static byte[] _nullDeposit = new byte[ByteLength.DepositLength];

        public static Deposit? DecodeDeposit(Span<byte> span)
        {
            if (span.Length != ByteLength.DepositLength) ThrowSourceLength<Deposit>(span.Length, ByteLength.DepositLength);
            if (span.SequenceEqual(_nullDeposit)) return null;
            Hash32[] proof = DecodeHashes(span.Slice(0, ByteLength.DepositLengthOfProof));
            DepositData data = DecodeDepositData(span.Slice(ByteLength.DepositLengthOfProof));
            Deposit deposit = new Deposit(proof, data);
            return deposit;
        }
    }
}