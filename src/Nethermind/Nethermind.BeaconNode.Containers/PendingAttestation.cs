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
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class PendingAttestation
    {
        public PendingAttestation(
            BitArray aggregationBits,
            AttestationData data,
            Slot inclusionDelay,
            ValidatorIndex proposerIndex)
        {
            AggregationBits = aggregationBits;
            Data = data;
            InclusionDelay = inclusionDelay;
            ProposerIndex = proposerIndex;
        }

        public BitArray AggregationBits { get; }

        public AttestationData Data { get; }

        /// <summary>Gets a challengable bit (SSZ-bool, 1 byte) for the custody of crosslink data</summary>
        public Slot InclusionDelay { get; }

        public ValidatorIndex ProposerIndex { get; }

        public static PendingAttestation Clone(PendingAttestation other)
        {
            var clone = new PendingAttestation(
                new BitArray(other.AggregationBits),
                AttestationData.Clone(other.Data),
                other.InclusionDelay,
                other.ProposerIndex);
            return clone;
        }
    }
}
