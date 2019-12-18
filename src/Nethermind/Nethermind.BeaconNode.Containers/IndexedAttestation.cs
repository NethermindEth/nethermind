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

using System.Collections.Generic;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class IndexedAttestation
    {
        private List<ValidatorIndex> _custodyBit0Indices;
        private List<ValidatorIndex> _custodyBit1Indices;

        public IndexedAttestation(
            IEnumerable<ValidatorIndex> custodyBit0Indices,
            IEnumerable<ValidatorIndex> custodyBit1Indices,
            AttestationData data,
            BlsSignature signature)
        {
            _custodyBit0Indices = new List<ValidatorIndex>(custodyBit0Indices);
            _custodyBit1Indices = new List<ValidatorIndex>(custodyBit1Indices);
            Data = data;
            Signature = signature;
        }

        /// <summary>Gets indices with custody bit equal to 0</summary>
        public IList<ValidatorIndex> CustodyBit0Indices { get { return _custodyBit0Indices; } }

        /// <summary>Gets indices with custody bit equal to 1</summary>
        public IList<ValidatorIndex> CustodyBit1Indices { get { return _custodyBit1Indices; } }

        public AttestationData Data { get; }

        public BlsSignature Signature { get; }

        public override string ToString()
        {
            return $"C:{Data.Index} S:{Data.Slot} Sig:{Signature.ToString().Substring(0, 12)}";
        }
    }
}
