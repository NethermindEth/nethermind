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
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int BeaconBlockBodyDynamicOffset = Ssz.BlsSignatureLength + Ssz.Eth1DataLength + Bytes32.Length + 5 * sizeof(uint);

        public static int BeaconBlockBodyLength(BeaconBlockBody container)
        {
            int result = BeaconBlockBodyDynamicOffset;

            result += Ssz.ProposerSlashingLength * container.ProposerSlashings.Count;
            result += Ssz.DepositLength() * container.Deposits.Count;
            result += Ssz.SignedVoluntaryExitLength * container.VoluntaryExits.Count;

            result += sizeof(uint) * container.AttesterSlashings.Count;
            for (int i = 0; i < container.AttesterSlashings.Count; i++)
            {
                result += Ssz.AttesterSlashingLength(container.AttesterSlashings[i]);
            }

            result += sizeof(uint) * container.Attestations.Count;
            for (int i = 0; i < container.Attestations.Count; i++)
            {
                result += Ssz.AttestationLength(container.Attestations[i]);
            }

            return result;
        }

        public static BeaconBlockBody DecodeBeaconBlockBody(ReadOnlySpan<byte> span)
        {
            int offset = 0;
            return DecodeBeaconBlockBody(span, ref offset);
        }

        public static void Encode(Span<byte> span, BeaconBlockBody container)
        {
            int offset = 0;
            Encode(span, container, ref offset);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BeaconBlockBody DecodeBeaconBlockBody(ReadOnlySpan<byte> span, ref int offset)
        {
            // static part
            
            BlsSignature randaoReveal = DecodeBlsSignature(span, ref offset);
            Eth1Data eth1Data = DecodeEth1Data(span, ref offset);
            Bytes32 graffiti = DecodeBytes32(span, ref offset);
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
            
            ProposerSlashing[] proposerSlashings = DecodeProposerSlashings(span.Slice(dynamicOffset1, proposerSlashingsLength));
            AttesterSlashing[] attesterSlashings = DecodeAttesterSlashings(span.Slice(dynamicOffset2, attesterSlashingsLength));
            Attestation[] attestations = DecodeAttestations(span.Slice(dynamicOffset3, attestationsLength));
            Deposit[] deposits = DecodeDeposits(span.Slice(dynamicOffset4, depositsLength));
            offset = dynamicOffset5;
            SignedVoluntaryExit[] signedVoluntaryExits = DecodeSignedVoluntaryExitVector(span, ref offset, span.Length);
            
            BeaconBlockBody container = new BeaconBlockBody(randaoReveal,
                eth1Data,
                graffiti,
                proposerSlashings,
                attesterSlashings,
                attestations,
                deposits,
                signedVoluntaryExits);
            return container;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, BeaconBlockBody container, ref int offset)
        {
            // Semantics of Encode = write container into span at offset, then increase offset by the bytes written
            
            // Static
            int dynamicOffset = Ssz.BeaconBlockBodyDynamicOffset;
            Encode(span, container.RandaoReveal, ref offset);
            Encode(span, container.Eth1Data, ref offset);
            Encode(span, container.Graffiti.AsSpan().ToArray(), ref offset);

            Encode(span, dynamicOffset, ref offset);
            int proposerSlashingsLength = container.ProposerSlashings.Count * Ssz.ProposerSlashingLength;
            dynamicOffset += proposerSlashingsLength;
            
            Encode(span, dynamicOffset, ref offset);
            int attesterSlashingsLength = container.AttesterSlashings.Sum(x => Ssz.AttesterSlashingLength(x) + VarOffsetSize);
            dynamicOffset += attesterSlashingsLength;
            
            Encode(span, dynamicOffset, ref offset);
            dynamicOffset += container.Attestations.Sum(x => Ssz.AttestationLength(x) + VarOffsetSize);
            
            Encode(span, dynamicOffset, ref offset);
            int depositsLength = container.Deposits.Count * Ssz.DepositLength();
            dynamicOffset += depositsLength;
            
            Encode(span, dynamicOffset, ref offset);
            int voluntaryExitsLength = container.VoluntaryExits.Count * Ssz.VoluntaryExitLength;
            dynamicOffset += voluntaryExitsLength;
            
            // Dynamic
            Encode(span.Slice(offset, proposerSlashingsLength), container.ProposerSlashings.ToArray());
            offset += proposerSlashingsLength;
            
            Encode(span.Slice(offset, attesterSlashingsLength), container.AttesterSlashings.ToArray());
            offset += attesterSlashingsLength;
            
            Encode(span, container.Attestations, ref offset);
            
            Encode(span.Slice(offset, depositsLength), container.Deposits.ToArray());
            offset += depositsLength;
            
            EncodeList(span, container.VoluntaryExits, ref offset);
        }
    }
}