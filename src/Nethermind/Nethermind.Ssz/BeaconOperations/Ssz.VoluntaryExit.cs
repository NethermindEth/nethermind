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
        public static void Encode(Span<byte> span, VoluntaryExit[] containers)
        {
            if (span.Length != VoluntaryExit.SszLength * containers.Length)
            {
                ThrowTargetLength<VoluntaryExit>(span.Length, VoluntaryExit.SszLength);
            }

            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span.Slice(i * VoluntaryExit.SszLength, VoluntaryExit.SszLength), containers[i]);
            }
        }

        public static VoluntaryExit?[] DecodeVoluntaryExits(Span<byte> span)
        {
            if (span.Length % VoluntaryExit.SszLength != 0)
            {
                ThrowInvalidSourceArrayLength<VoluntaryExit>(span.Length, VoluntaryExit.SszLength);
            }

            int count = span.Length / VoluntaryExit.SszLength;
            VoluntaryExit?[] containers = new VoluntaryExit?[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeVoluntaryExit(span.Slice(i * VoluntaryExit.SszLength, VoluntaryExit.SszLength));
            }

            return containers;
        }
        
        private static void Encode(Span<byte> span, VoluntaryExit[] containers, ref int offset, ref int dynamicOffset)
        {
            int length = containers.Length * VoluntaryExit.SszLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length), containers);
            dynamicOffset += length;
            offset += VarOffsetSize;
        }
        
        public static void Encode(Span<byte> span, VoluntaryExit container)
        {
            if (span.Length != VoluntaryExit.SszLength) ThrowTargetLength<VoluntaryExit>(span.Length, VoluntaryExit.SszLength);
            if (container == null) return;
            int offset = 0;
            Encode(span, container.Epoch, ref offset);
            Encode(span, container.ValidatorIndex, ref offset);
            Encode(span, container.Signature, ref offset);
        }

        private static byte[] _nullVoluntaryExit = new byte[VoluntaryExit.SszLength];

        public static VoluntaryExit? DecodeVoluntaryExit(Span<byte> span)
        {
            if (span.Length != VoluntaryExit.SszLength) ThrowSourceLength<VoluntaryExit>(span.Length, VoluntaryExit.SszLength);
            if (span.SequenceEqual(_nullVoluntaryExit)) return null;
            int offset = 0;
            VoluntaryExit container = new VoluntaryExit();
            container.Epoch = DecodeEpoch(span, ref offset);
            container.ValidatorIndex = DecodeValidatorIndex(span, ref offset);
            container.Signature = DecodeBlsSignature(span, ref offset);
            return container;
        }
    }
}