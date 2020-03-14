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
        public const int Eth1DataLength = Ssz.RootLength + sizeof(ulong) + Ssz.Bytes32Length;

        private static Eth1Data DecodeEth1Data(ReadOnlySpan<byte> span, ref int offset)
        {
            Eth1Data eth1Data = DecodeEth1Data(span.Slice(offset, Ssz.Eth1DataLength));
            offset += Ssz.Eth1DataLength;
            return eth1Data;
        }
        
        public static void Encode(Span<byte> span, Eth1Data[] containers)
        {
            if (span.Length != Ssz.Eth1DataLength * containers.Length)
            {
                ThrowTargetLength<Eth1Data>(span.Length, Ssz.Eth1DataLength);
            }

            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span.Slice(i * Ssz.Eth1DataLength, Ssz.Eth1DataLength), containers[i]);
            }
        }
        
        private static void Encode(Span<byte> span, Eth1Data value, ref int offset)
        {
            Encode(span.Slice(offset, Ssz.Eth1DataLength), value);
            offset += Ssz.Eth1DataLength;
        }

        public static void Encode(Span<byte> span, Eth1Data container)
        {
            if (span.Length != Ssz.Eth1DataLength) ThrowTargetLength<Eth1Data>(span.Length, Ssz.Eth1DataLength);
            int offset = 0;
            Encode(span, container.DepositRoot, ref offset);
            Encode(span, container.DepositCount, ref offset);
            Encode(span, container.BlockHash, ref offset);
        }

        public static Eth1Data DecodeEth1Data(ReadOnlySpan<byte> span)
        {
            if (span.Length != Ssz.Eth1DataLength) ThrowSourceLength<Eth1Data>(span.Length, Ssz.Eth1DataLength);
            Root depositRoot = DecodeRoot(span.Slice(0, Ssz.RootLength));
            ulong depositCount = DecodeULong(span.Slice(Ssz.RootLength, sizeof(ulong)));
            Bytes32 blockHash = DecodeBytes32(span.Slice(Ssz.RootLength + sizeof(ulong), Ssz.Bytes32Length));
            Eth1Data container = new Eth1Data(depositRoot, depositCount, blockHash);
            return container;
        }

        public static Eth1Data[] DecodeEth1Datas(Span<byte> span)
        {
            if (span.Length % Ssz.Eth1DataLength != 0) ThrowInvalidSourceArrayLength<Eth1Data>(span.Length, Ssz.Eth1DataLength);
            int count = span.Length / Ssz.Eth1DataLength;
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