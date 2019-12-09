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
        public static void Encode(Span<byte> span, BeaconBlockBody? container)
        {
            if (container is null)
            {
                return;
            }
            
            int offset = 0;
            int dynamicOffset = BeaconBlockBody.SszDynamicOffset;
            Encode(span, container.RandaoReversal, ref offset);
            Encode(span, container.Eth1Data, ref offset);
            Encode(span, container.Graffiti, ref offset);
            Encode(span, container.ProposerSlashings, ref offset, ref dynamicOffset);
            Encode(span, container.AttesterSlashings, ref offset, ref dynamicOffset);
            Encode(span, container.Attestations, ref offset, ref dynamicOffset);
            Encode(span, container.Deposits, ref offset, ref dynamicOffset);
            Encode(span, container.VoluntaryExits, ref offset, ref dynamicOffset);
        }
        
        public static BeaconBlockBody DecodeBeaconBlockBody(Span<byte> span)
        {
            // static part
            
            int offset = 0;
            BeaconBlockBody container = new BeaconBlockBody();
            container.RandaoReversal = DecodeBlsSignature(span, ref offset);
            container.Eth1Data = DecodeEth1Data(span, ref offset);
            container.Graffiti = DecodeBytes32(span, ref offset);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset1);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset2);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset3);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset4);
            DecodeDynamicOffset(span, ref offset, out int dynamicOffset5);

            // var part
            
            int proposerSlashingsLength = dynamicOffset2 - dynamicOffset1;
            int attesterSlashingsLength = dynamicOffset3 - dynamicOffset2;
            int attestationsLength = dynamicOffset4 - dynamicOffset3;
            int depositsLength = dynamicOffset5 - dynamicOffset4;
            int voluntaryExitsLength = span.Length - dynamicOffset5;

            container.ProposerSlashings = DecodeProposerSlashings(span.Slice(dynamicOffset1, proposerSlashingsLength));
            container.AttesterSlashings = DecodeAttesterSlashings(span.Slice(dynamicOffset2, attesterSlashingsLength));
            container.Attestations = DecodeAttestations(span.Slice(dynamicOffset3, attestationsLength));
            container.Deposits = DecodeDeposits(span.Slice(dynamicOffset4, depositsLength));
            container.VoluntaryExits = DecodeVoluntaryExits(span.Slice(dynamicOffset5, voluntaryExitsLength));
            
            return container;
        }
    }
}