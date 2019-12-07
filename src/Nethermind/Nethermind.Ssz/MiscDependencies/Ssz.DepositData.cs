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
        public static void Encode(Span<byte> span, DepositData? container)
        {
            if (container is null)
            {
                return;
            }
            
            if (span.Length != DepositData.SszLength) ThrowTargetLength<DepositData>(span.Length, DepositData.SszLength);
            int offset = 0;
            Encode(span, container.PublicKey, ref offset);
            Encode(span, container.WithdrawalCredentials, ref offset);
            Encode(span, container.Amount, ref offset);
            Encode(span, container.Signature, ref offset);
        }

        public static DepositData DecodeDepositData(Span<byte> span)
        {
            if (span.Length != DepositData.SszLength) ThrowSourceLength<DepositData>(span.Length, DepositData.SszLength);
            int offset = 0;
            DepositData container = new DepositData();
            container.PublicKey = DecodeBlsPublicKey(span, ref offset);
            container.WithdrawalCredentials = DecodeSha256(span, ref offset);
            container.Amount = DecodeGwei(span, ref offset);
            container.Signature = DecodeBlsSignature(span, ref offset);
            return container;
        }
    }
}