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

using System.Collections;
using Nethermind.Core2.Crypto;

namespace Nethermind.BeaconNode.Containers
{
    public class Attestation
    {
        public Attestation(BitArray aggregationBits, AttestationData data, BitArray custodyBits, BlsSignature signature)
        {
            AggregationBits = aggregationBits;
            Data = data;
            CustodyBits = custodyBits;
            Signature = signature;
        }

        public BitArray AggregationBits { get; }

        public BitArray CustodyBits { get; }

        public AttestationData Data { get; }

        public BlsSignature Signature { get; private set; }

        public void SetSignature(BlsSignature signature)
        {
            Signature = signature;
        }

        public override string ToString()
        {
            return $"C:{Data.Index} S:{Data.Slot} Sig:{Signature.ToString().Substring(0, 12)}";
        }
    }
}
