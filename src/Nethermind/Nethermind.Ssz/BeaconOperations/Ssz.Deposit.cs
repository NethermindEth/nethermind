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
using Nethermind.Core2.Containers;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static void Encode(Span<byte> span, Deposit[]? containers)
        {
            if (span.Length != Deposit.SszLength * (containers?.Length ?? 0))
            {
                ThrowTargetLength<Deposit>(span.Length, Deposit.SszLength);
            }

            if (!(containers is null))
            {
                for (int i = 0; i < containers.Length; i++)
                {
                    Encode(span.Slice(i * Deposit.SszLength, Deposit.SszLength), containers[i]);
                }
            }
        }

        public static Deposit[] DecodeDeposits(Span<byte> span)
        {
            if (span.Length % Deposit.SszLength != 0)
            {
                ThrowInvalidSourceArrayLength<Deposit>(span.Length, Deposit.SszLength);
            }

            int count = span.Length / Deposit.SszLength;
            Deposit[] containers = new Deposit[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeDeposit(span.Slice(i * Deposit.SszLength, Deposit.SszLength));
            }

            return containers;
        }
        
        private static void Encode(Span<byte> span, Deposit[]? containers, ref int offset, ref int dynamicOffset)
        {
            int length = (containers?.Length ?? 0) * Deposit.SszLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length), containers);
            dynamicOffset += length;
            offset += VarOffsetSize;
        }
        
        public static void Encode(Span<byte> span, Deposit? container)
        {
            if (span.Length != Deposit.SszLength) ThrowTargetLength<Deposit>(span.Length, Deposit.SszLength);
            if (container == null) return;
            Encode(span.Slice(0, Deposit.SszLengthOfProof), container.Proof);
            Encode(span.Slice(Deposit.SszLengthOfProof), container.Data);
        }

        private static byte[] _nullDeposit = new byte[Deposit.SszLength];

        public static Deposit? DecodeDeposit(Span<byte> span)
        {
            if (span.Length != Deposit.SszLength) ThrowSourceLength<Deposit>(span.Length, Deposit.SszLength);
            if (span.SequenceEqual(_nullDeposit)) return null;
            Deposit deposit = new Deposit();
            deposit.Proof = DecodeHashes(span.Slice(0, Deposit.SszLengthOfProof));
            deposit.Data = DecodeDepositData(span.Slice(Deposit.SszLengthOfProof));
            return deposit;
        }
    }
}