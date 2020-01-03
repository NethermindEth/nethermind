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
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        private static void Encode(Span<byte> span, ProposerSlashing?[]? containers, ref int offset, ref int dynamicOffset)
        {
            int length = (containers?.Length ?? 0) * ProposerSlashing.SszLength;
            Encode(span.Slice(offset, VarOffsetSize), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length), containers);
            dynamicOffset += length;
            offset += VarOffsetSize;
        }
        
         public static void Encode(Span<byte> span, ProposerSlashing? container)
        {
            if (container is null)
            {
                return;
            }
            
            if (span.Length != ProposerSlashing.SszLength)
            {
                ThrowTargetLength<ProposerSlashing>(span.Length, ProposerSlashing.SszLength);
            }

            if (container == null)
            {
                return;
            }

            int offset = 0;
            Encode(span.Slice(0, ByteLength.ValidatorIndex), container.ProposerIndex);
            offset += ByteLength.ValidatorIndex;
            Encode(span.Slice(offset, BeaconBlockHeader.SszLength), container.Header1);
            offset += BeaconBlockHeader.SszLength;
            Encode(span.Slice(offset, BeaconBlockHeader.SszLength), container.Header2);
        }

        private static byte[] _nullProposerSlashing = new byte[ProposerSlashing.SszLength];

        public static ProposerSlashing? DecodeProposerSlashing(Span<byte> span)
        {
            if (span.Length != ProposerSlashing.SszLength) ThrowSourceLength<ProposerSlashing>(span.Length, ProposerSlashing.SszLength);
            if (span.SequenceEqual(_nullProposerSlashing)) return null;
            int offset = 0;
            ProposerSlashing container = new ProposerSlashing();
            container.ProposerIndex = DecodeValidatorIndex(span, ref offset);
            container.Header1 = DecodeBeaconBlockHeader(span, ref offset);
            container.Header2 = DecodeBeaconBlockHeader(span, ref offset);
            return container;
        }

        public static void Encode(Span<byte> span, ProposerSlashing?[]? containers)
        {
            if (containers is null)
            {
                return;
            }
            
            if (span.Length != ProposerSlashing.SszLength * containers.Length)
            {
                ThrowTargetLength<ProposerSlashing>(span.Length, ProposerSlashing.SszLength);
            }

            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span.Slice(i * ProposerSlashing.SszLength, ProposerSlashing.SszLength), containers[i]);
            }
        }

        public static ProposerSlashing?[] DecodeProposerSlashings(Span<byte> span)
        {
            if (span.Length % ProposerSlashing.SszLength != 0)
            {
                ThrowInvalidSourceArrayLength<ProposerSlashing>(span.Length, ProposerSlashing.SszLength);
            }

            int count = span.Length / ProposerSlashing.SszLength;
            ProposerSlashing?[] containers = new ProposerSlashing[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeProposerSlashing(span.Slice(i * ProposerSlashing.SszLength, ProposerSlashing.SszLength));
            }

            return containers;
        }
    }
}