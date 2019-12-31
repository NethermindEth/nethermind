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
using System.Collections.ObjectModel;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.HonestValidator
{
    public class ValidatorState
    {
        private readonly Dictionary<BlsPublicKey, Shard> _attestationShard = new Dictionary<BlsPublicKey, Shard>();
        private readonly Dictionary<BlsPublicKey, Slot> _attestationSlot = new Dictionary<BlsPublicKey, Slot>();
        private readonly Dictionary<BlsPublicKey, Slot> _proposalSlot = new Dictionary<BlsPublicKey, Slot>();

        public IReadOnlyDictionary<BlsPublicKey, Shard> AttestationShard => _attestationShard;
        public IReadOnlyDictionary<BlsPublicKey, Slot> AttestationSlot => _attestationSlot;
        public IReadOnlyDictionary<BlsPublicKey, Slot> ProposalSlot => _proposalSlot;

        public void SetAttestationDuty(BlsPublicKey key, Slot slot, Shard shard)
        {
            // TODO: can look ahead to next epoch
            _attestationShard[key] = shard;
            _attestationSlot[key] = slot;
        }

        public void SetProposalDuty(BlsPublicKey key, Slot slot)
        {
            _proposalSlot[key] = slot;
        }
    }
}