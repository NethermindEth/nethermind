// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
