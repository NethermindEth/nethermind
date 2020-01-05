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
            int length = (containers?.Length ?? 0) * ByteLength.ProposerSlashingLength;
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
            
            if (span.Length != ByteLength.ProposerSlashingLength)
            {
                ThrowTargetLength<ProposerSlashing>(span.Length, ByteLength.ProposerSlashingLength);
            }

            if (container == null)
            {
                return;
            }

            int offset = 0;
            Encode(span.Slice(0, ByteLength.ValidatorIndexLength), container.ProposerIndex);
            offset += ByteLength.ValidatorIndexLength;
            Encode(span.Slice(offset, ByteLength.BeaconBlockHeaderLength), container.Header1);
            offset += ByteLength.BeaconBlockHeaderLength;
            Encode(span.Slice(offset, ByteLength.BeaconBlockHeaderLength), container.Header2);
        }

        private static byte[] _nullProposerSlashing = new byte[ByteLength.ProposerSlashingLength];

        public static ProposerSlashing? DecodeProposerSlashing(Span<byte> span)
        {
            if (span.Length != ByteLength.ProposerSlashingLength) ThrowSourceLength<ProposerSlashing>(span.Length, ByteLength.ProposerSlashingLength);
            if (span.SequenceEqual(_nullProposerSlashing)) return null;
            int offset = 0;
            ValidatorIndex proposerIndex = DecodeValidatorIndex(span, ref offset);
            BeaconBlockHeader header1 = DecodeBeaconBlockHeader(span, ref offset);
            BeaconBlockHeader header2 = DecodeBeaconBlockHeader(span, ref offset);
            ProposerSlashing container = new ProposerSlashing(proposerIndex, header1, header2);
            return container;
        }

        public static void Encode(Span<byte> span, ProposerSlashing?[]? containers)
        {
            if (containers is null)
            {
                return;
            }
            
            if (span.Length != ByteLength.ProposerSlashingLength * containers.Length)
            {
                ThrowTargetLength<ProposerSlashing>(span.Length, ByteLength.ProposerSlashingLength);
            }

            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span.Slice(i * ByteLength.ProposerSlashingLength, ByteLength.ProposerSlashingLength), containers[i]);
            }
        }

        public static ProposerSlashing?[] DecodeProposerSlashings(Span<byte> span)
        {
            if (span.Length % ByteLength.ProposerSlashingLength != 0)
            {
                ThrowInvalidSourceArrayLength<ProposerSlashing>(span.Length, ByteLength.ProposerSlashingLength);
            }

            int count = span.Length / ByteLength.ProposerSlashingLength;
            ProposerSlashing?[] containers = new ProposerSlashing[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeProposerSlashing(span.Slice(i * ByteLength.ProposerSlashingLength, ByteLength.ProposerSlashingLength));
            }

            return containers;
        }
    }
}