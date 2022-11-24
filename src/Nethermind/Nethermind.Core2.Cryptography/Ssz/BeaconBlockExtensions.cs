// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class BeaconBlockExtensions
    {
        public static Root HashTreeRoot(this BeaconBlock item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee)
        {
            var tree = new SszTree(item.ToSszContainer(maximumProposerSlashings, maximumAttesterSlashings, maximumAttestations, maximumDeposits, maximumVoluntaryExits, maximumValidatorsPerCommittee));
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlock item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee)
        {
            return new SszContainer(GetValues(item, maximumProposerSlashings, maximumAttesterSlashings, maximumAttestations, maximumDeposits, maximumVoluntaryExits, maximumValidatorsPerCommittee));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlock item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee)
        {
            yield return item.Slot.ToSszBasicElement();
            yield return item.ParentRoot.ToSszBasicVector();
            yield return item.StateRoot.ToSszBasicVector();
            yield return item.Body.ToSszContainer(maximumProposerSlashings, maximumAttesterSlashings, maximumAttestations, maximumDeposits, maximumVoluntaryExits, maximumValidatorsPerCommittee);
        }
    }
}
