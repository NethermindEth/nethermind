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
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class IndexedAttestationExtensions
    {
        public static SszContainer ToSszContainer(this IndexedAttestation item, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters));
        }

        private static IEnumerable<SszElement> GetValues(IndexedAttestation item, MiscellaneousParameters miscellaneousParameters)
        {
            yield return item.CustodyBit0Indices.ToSszBasicList(miscellaneousParameters.MaximumValidatorsPerCommittee);
            yield return item.CustodyBit1Indices.ToSszBasicList(miscellaneousParameters.MaximumValidatorsPerCommittee);
            yield return item.Data.ToSszContainer();
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
