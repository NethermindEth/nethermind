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