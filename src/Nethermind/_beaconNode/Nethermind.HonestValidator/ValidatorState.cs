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

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.HonestValidator
{
    public class ValidatorState
    {
        private readonly ConcurrentDictionary<Slot, IList<(BlsPublicKey, CommitteeIndex)>> _attestationDutyBySlot =
            new ConcurrentDictionary<Slot, IList<(BlsPublicKey, CommitteeIndex)>>();

        private readonly Dictionary<BlsPublicKey, (Slot, CommitteeIndex)> _attestationSlotAndIndex =
            new Dictionary<BlsPublicKey, (Slot, CommitteeIndex)>();

        private readonly Dictionary<Slot, BlsPublicKey> _proposalDutyBySlot = new Dictionary<Slot, BlsPublicKey>();
        private readonly Dictionary<BlsPublicKey, Slot> _proposalSlot = new Dictionary<BlsPublicKey, Slot>();

        public IReadOnlyDictionary<BlsPublicKey, (Slot, CommitteeIndex)> AttestationSlotAndIndex =>
            _attestationSlotAndIndex;

        public IReadOnlyDictionary<BlsPublicKey, Slot> ProposalSlot => _proposalSlot;

        public void ClearAttestationDutyForSlot(Slot slot)
        {
            _attestationDutyBySlot.TryRemove(slot, out _);
        }

        public void ClearProposalDutyForSlot(Slot slot)
        {
            _proposalDutyBySlot.Remove(slot);
        }

        public IList<(BlsPublicKey, CommitteeIndex)> GetAttestationDutyForSlot(Slot slot)
        {
            // TODO: Consider TryRemove that pulls it out of the dictionary for processing
            return _attestationDutyBySlot.GetValueOrDefault(slot) ?? new (BlsPublicKey, CommitteeIndex)[0];
        }

        public BlsPublicKey? GetProposalDutyForSlot(Slot slot)
        {
            return _proposalDutyBySlot.GetValueOrDefault(slot);
        }

        public void SetAttestationDuty(BlsPublicKey key, Slot slot, CommitteeIndex index)
        {
            // TODO: can look ahead to next epoch
            _attestationSlotAndIndex[key] = (slot, index);

            IList<(BlsPublicKey, CommitteeIndex)> list =
                _attestationDutyBySlot.GetOrAdd(slot, slot => new List<(BlsPublicKey, CommitteeIndex)>());
            list.Add((key, index));
        }

        public void SetProposalDuty(BlsPublicKey key, Slot slot)
        {
            _proposalSlot[key] = slot;
            _proposalDutyBySlot[slot] = key;
        }
    }
}