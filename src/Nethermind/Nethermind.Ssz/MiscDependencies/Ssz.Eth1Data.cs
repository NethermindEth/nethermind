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
        private static Eth1Data DecodeEth1Data(Span<byte> span, ref int offset)
        {
            Eth1Data eth1Data = DecodeEth1Data(span.Slice(offset, Eth1Data.SszLength));
            offset += Eth1Data.SszLength;
            return eth1Data;
        }
        
        public static void Encode(Span<byte> span, Eth1Data[]? containers)
        {
            if (containers is null)
            {
                return;
            }
            
            if (span.Length != Eth1Data.SszLength * containers.Length)
            {
                ThrowTargetLength<Eth1Data>(span.Length, Eth1Data.SszLength);
            }

            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span.Slice(i * Eth1Data.SszLength, Eth1Data.SszLength), containers[i]);
            }
        }
        
        private static void Encode(Span<byte> span, Eth1Data? value, ref int offset)
        {
            Encode(span.Slice(offset, Eth1Data.SszLength), value);
            offset += Eth1Data.SszLength;
        }

        public static void Encode(Span<byte> span, Eth1Data? container)
        {
            if (span.Length != Eth1Data.SszLength) ThrowTargetLength<Eth1Data>(span.Length, Eth1Data.SszLength);
            if (container == null) return;
            int offset = 0;
            Encode(span, container.DepositRoot, ref offset);
            Encode(span, container.DepositCount, ref offset);
            Encode(span, container.BlockHash, ref offset);
        }

        public static Eth1Data DecodeEth1Data(Span<byte> span)
        {
            if (span.Length != Eth1Data.SszLength) ThrowSourceLength<Eth1Data>(span.Length, Eth1Data.SszLength);
            Eth1Data container = new Eth1Data();
            container.DepositRoot = DecodeSha256(span.Slice(0, ByteLength.Hash32Length));
            container.DepositCount = DecodeULong(span.Slice(ByteLength.Hash32Length, sizeof(ulong)));
            container.BlockHash = DecodeSha256(span.Slice(ByteLength.Hash32Length + sizeof(ulong), ByteLength.Hash32Length));
            return container;
        }

        public static Eth1Data[] DecodeEth1Datas(Span<byte> span)
        {
            if (span.Length % Eth1Data.SszLength != 0) ThrowInvalidSourceArrayLength<Eth1Data>(span.Length, Eth1Data.SszLength);
            int count = span.Length / Eth1Data.SszLength;
            Eth1Data[] containers = new Eth1Data[count];
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeEth1Data(span, ref offset);
            }

            return containers;
        }
    }
}