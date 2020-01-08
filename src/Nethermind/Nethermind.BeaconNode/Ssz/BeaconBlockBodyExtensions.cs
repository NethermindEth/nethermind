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
    public static class BeaconBlockBodyExtensions
    {
        public static Hash32 HashTreeRoot(this BeaconBlockBody item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            var tree = new SszTree(item.ToSszContainer(miscellaneousParameters, maxOperationsPerBlock));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlockBody item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters, maxOperationsPerBlock));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlockBody item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            yield return item.RandaoReveal.ToSszBasicVector();
            yield return item.Eth1Data.ToSszContainer();
            yield return item.Graffiti.ToSszBasicVector();
            // Operations
            yield return item.ProposerSlashings.ToSszList(maxOperationsPerBlock.MaximumProposerSlashings);
            yield return item.AttesterSlashings.ToSszList(maxOperationsPerBlock.MaximumAttesterSlashings, miscellaneousParameters);
            yield return item.Attestations.ToSszList(maxOperationsPerBlock.MaximumAttestations, miscellaneousParameters);
            yield return item.Deposits.ToSszList(maxOperationsPerBlock.MaximumDeposits);
            yield return item.VoluntaryExits.ToSszList(maxOperationsPerBlock.MaximumVoluntaryExits);
            //yield return item.Transfers.ToSszList(MAX_TRANSFERS);
        }
    }
}
