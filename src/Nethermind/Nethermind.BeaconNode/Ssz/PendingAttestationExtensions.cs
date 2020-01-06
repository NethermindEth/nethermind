﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class PendingAttestationExtensions
    {
        public static SszContainer ToSszContainer(this PendingAttestation item, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters));
        }

        public static SszList ToSszList(this IEnumerable<PendingAttestation> list, ulong limit, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszList(list.Select(x => x.ToSszContainer(miscellaneousParameters)), limit);
        }

        private static IEnumerable<SszElement> GetValues(PendingAttestation item, MiscellaneousParameters miscellaneousParameters)
        {
            yield return item.AggregationBits.ToSszBitlist(miscellaneousParameters.MaximumValidatorsPerCommittee);
            yield return item.Data.ToSszContainer();
            yield return item.InclusionDelay.ToSszBasicElement();
            yield return item.ProposerIndex.ToSszBasicElement();
        }
    }
}
