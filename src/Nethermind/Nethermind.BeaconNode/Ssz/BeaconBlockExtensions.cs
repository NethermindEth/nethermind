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
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class BeaconBlockExtensions
    {
        public static Hash32 HashTreeRoot(this BeaconBlock item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee)
        {
            var tree = new SszTree(item.ToSszContainer(maximumProposerSlashings, maximumAttesterSlashings, maximumAttestations, maximumDeposits, maximumVoluntaryExits, maximumValidatorsPerCommittee));
            return new Hash32(tree.HashTreeRoot());
        }

        public static Hash32 SigningRoot(this BeaconBlock item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, maximumProposerSlashings, maximumAttesterSlashings, maximumAttestations, maximumDeposits, maximumVoluntaryExits, maximumValidatorsPerCommittee, true)));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlock item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee)
        {
            return new SszContainer(GetValues(item, maximumProposerSlashings, maximumAttesterSlashings, maximumAttestations, maximumDeposits, maximumVoluntaryExits, maximumValidatorsPerCommittee, false));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlock item, ulong maximumProposerSlashings, ulong maximumAttesterSlashings, ulong maximumAttestations, ulong maximumDeposits, ulong maximumVoluntaryExits, ulong maximumValidatorsPerCommittee, bool forSigning)
        {
            yield return item.Slot.ToSszBasicElement();
            yield return item.ParentRoot.ToSszBasicVector();
            yield return item.StateRoot.ToSszBasicVector();
            yield return item.Body.ToSszContainer(maximumProposerSlashings, maximumAttesterSlashings, maximumAttestations, maximumDeposits, maximumVoluntaryExits, maximumValidatorsPerCommittee);
            if (!forSigning)
            {
                //signature: BLSSignature
                yield return item.Signature.ToSszBasicVector();
            }
        }
    }
}
