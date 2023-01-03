// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int DepositDataLength = Ssz.BlsPublicKeyLength + Ssz.Bytes32Length + Ssz.GweiLength + Ssz.BlsSignatureLength;

        public static void Encode(Span<byte> span, DepositData? container)
        {
            if (container is null)
            {
                return;
            }

            if (span.Length != Ssz.DepositDataLength) ThrowTargetLength<DepositData>(span.Length, Ssz.DepositDataLength);
            int offset = 0;
            Encode(span, container.PublicKey, ref offset);
            Encode(span, container.WithdrawalCredentials, ref offset);
            Encode(span, container.Amount, ref offset);
            Encode(span, container.Signature, ref offset);
        }

        public static DepositData DecodeDepositData(ReadOnlySpan<byte> span)
        {
            if (span.Length != Ssz.DepositDataLength) ThrowSourceLength<DepositData>(span.Length, Ssz.DepositDataLength);
            int offset = 0;
            BlsPublicKey publicKey = DecodeBlsPublicKey(span, ref offset);
            Bytes32 withdrawalCredentials = DecodeBytes32(span, ref offset);
            Gwei amount = DecodeGwei(span, ref offset);
            BlsSignature signature = DecodeBlsSignature(span, ref offset);
            DepositData container = new DepositData(publicKey, withdrawalCredentials, amount, signature);
            return container;
        }
    }
}
