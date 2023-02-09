// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class BeaconBlockBodyExtensions
    {
        public static Root HashTreeRoot(this BeaconBlockBody item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee)
        {
            var tree = new SszTree(item.ToSszContainer(maximumProposerSlashings, maximumAttesterSlashings, maximumAttestations, maximumDeposits, maximumVoluntaryExits, maximumValidatorsPerCommittee));
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlockBody item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee)
        {
            return new SszContainer(GetValues(item, maximumProposerSlashings, maximumAttesterSlashings, maximumAttestations, maximumDeposits, maximumVoluntaryExits, maximumValidatorsPerCommittee));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlockBody item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee)
        {
            yield return item.RandaoReveal.ToSszBasicVector();
            yield return item.Eth1Data.ToSszContainer();
            yield return item.Graffiti.ToSszBasicVector();
            // Operations
            yield return item.ProposerSlashings.ToSszList(maximumProposerSlashings);
            yield return item.AttesterSlashings.ToSszList(maximumAttesterSlashings, maximumValidatorsPerCommittee);
            yield return item.Attestations.ToSszList(maximumAttestations, maximumValidatorsPerCommittee);
            yield return item.Deposits.ToSszList(maximumDeposits);
            yield return item.VoluntaryExits.ToSszList(maximumVoluntaryExits);
            //yield return item.Transfers.ToSszList(MAX_TRANSFERS);
        }
    }
}
