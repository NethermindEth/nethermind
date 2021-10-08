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
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static int DepositLengthOfProof()
        {
            return (DepositContractTreeDepth + 1) * Ssz.Bytes32Length;
        }

        public static int DepositLength()
        {
           return DepositLengthOfProof() + Ssz.DepositDataLength;
        }

        public static void Encode(Span<byte> span, Deposit[]? containers)
        {
            if (span.Length != Ssz.DepositLength() * (containers?.Length ?? 0))
            {
                ThrowTargetLength<Deposit>(span.Length, Ssz.DepositLength());
            }

            if (!(containers is null))
            {
                for (int i = 0; i < containers.Length; i++)
                {
                    Encode(span.Slice(i * Ssz.DepositLength(), Ssz.DepositLength()), containers[i]);
                }
            }
        }

        public static Deposit[] DecodeDeposits(ReadOnlySpan<byte> span)
        {
            if (span.Length % Ssz.DepositLength() != 0)
            {
                ThrowInvalidSourceArrayLength<Deposit>(span.Length, Ssz.DepositLength());
            }

            int count = span.Length / Ssz.DepositLength();
            Deposit[] containers = new Deposit[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeDeposit(span.Slice(i * Ssz.DepositLength(), Ssz.DepositLength()));
            }

            return containers;
        }
        
        private static void Encode(Span<byte> span, Deposit[]? containers, ref int offset, ref int dynamicOffset)
        {
            int length = (containers?.Length ?? 0) * Ssz.DepositLength();
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length), containers);
            dynamicOffset += length;
            offset += VarOffsetSize;
        }
        
        public static void Encode(Span<byte> span, Deposit container)
        {
            if (span.Length != Ssz.DepositLength()) ThrowTargetLength<Deposit>(span.Length, Ssz.DepositLength());
            Encode(span.Slice(0, Ssz.DepositLengthOfProof()), container.Proof);
            Encode(span.Slice(Ssz.DepositLengthOfProof()), container.Data.Item);
        }

        public static Deposit DecodeDeposit(ReadOnlySpan<byte> span)
        {
            if (span.Length != Ssz.DepositLength()) ThrowSourceLength<Deposit>(span.Length, Ssz.DepositLength());
            Bytes32[] proof = DecodeBytes32s(span.Slice(0, Ssz.DepositLengthOfProof()));
            DepositData data = DecodeDepositData(span.Slice(Ssz.DepositLengthOfProof()));
            Deposit deposit = new Deposit(proof, data.OrRoot);
            return deposit;
        }
    }
}