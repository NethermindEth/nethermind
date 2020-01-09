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
        public const int VoluntaryExitLength = Ssz.EpochLength + Ssz.ValidatorIndexLength + Ssz.BlsSignatureLength;

        public static void Encode(Span<byte> span, VoluntaryExit[] containers)
        {
            if (span.Length != Ssz.VoluntaryExitLength * containers.Length)
            {
                ThrowTargetLength<VoluntaryExit>(span.Length, Ssz.VoluntaryExitLength);
            }

            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span.Slice(i * Ssz.VoluntaryExitLength, Ssz.VoluntaryExitLength), containers[i]);
            }
        }

        public static VoluntaryExit?[] DecodeVoluntaryExits(Span<byte> span)
        {
            if (span.Length % Ssz.VoluntaryExitLength != 0)
            {
                ThrowInvalidSourceArrayLength<VoluntaryExit>(span.Length, Ssz.VoluntaryExitLength);
            }

            int count = span.Length / Ssz.VoluntaryExitLength;
            VoluntaryExit?[] containers = new VoluntaryExit?[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeVoluntaryExit(span.Slice(i * Ssz.VoluntaryExitLength, Ssz.VoluntaryExitLength));
            }

            return containers;
        }
        
        private static void Encode(Span<byte> span, VoluntaryExit[] containers, ref int offset, ref int dynamicOffset)
        {
            int length = containers.Length * Ssz.VoluntaryExitLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length), containers);
            dynamicOffset += length;
            offset += VarOffsetSize;
        }
        
        public static void Encode(Span<byte> span, VoluntaryExit container)
        {
            if (span.Length != Ssz.VoluntaryExitLength) ThrowTargetLength<VoluntaryExit>(span.Length, Ssz.VoluntaryExitLength);
            if (container == null) return;
            int offset = 0;
            Encode(span, container.Epoch, ref offset);
            Encode(span, container.ValidatorIndex, ref offset);
            Encode(span, container.Signature, ref offset);
        }

        private static byte[] _nullVoluntaryExit = new byte[Ssz.VoluntaryExitLength];

        public static VoluntaryExit? DecodeVoluntaryExit(Span<byte> span)
        {
            if (span.Length != Ssz.VoluntaryExitLength) ThrowSourceLength<VoluntaryExit>(span.Length, Ssz.VoluntaryExitLength);
            if (span.SequenceEqual(_nullVoluntaryExit)) return null;
            int offset = 0;
            Epoch epoch = DecodeEpoch(span, ref offset);
            ValidatorIndex validatorIndex = DecodeValidatorIndex(span, ref offset);
            BlsSignature signature = DecodeBlsSignature(span, ref offset);
            VoluntaryExit container = new VoluntaryExit(epoch, validatorIndex, signature);
            return container;
        }
    }
}