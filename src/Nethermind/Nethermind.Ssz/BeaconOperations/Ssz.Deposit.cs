// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
