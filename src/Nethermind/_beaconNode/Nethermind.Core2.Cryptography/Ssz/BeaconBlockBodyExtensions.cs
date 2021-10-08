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
