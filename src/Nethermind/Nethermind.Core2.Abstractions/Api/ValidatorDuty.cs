// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Api
{
    public class ValidatorDuty
    {
        public ValidatorDuty(BlsPublicKey validatorPublicKey, Slot? attestationSlot, CommitteeIndex? attestationIndex,
            Slot? blockProposalSlot)
        {
            ValidatorPublicKey = validatorPublicKey;
            AttestationSlot = attestationSlot;
            AttestationIndex = attestationIndex;
            BlockProposalSlot = blockProposalSlot;
        }

        public CommitteeIndex? AttestationIndex { get; }
        public Slot? AttestationSlot { get; }
        public Slot? BlockProposalSlot { get; }
        public BlsPublicKey ValidatorPublicKey { get; }
    }
}
