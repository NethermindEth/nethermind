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
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class AttestationDataExtensions
    {
        public static Root HashTreeRoot(this AttestationData item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this AttestationData item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(AttestationData item)
        {
            yield return item.Slot.ToSszBasicElement();
            yield return item.Index.ToSszBasicElement();
            // LMD GHOST vote
            yield return item.BeaconBlockRoot.ToSszBasicVector();
            // FFG vote
            yield return item.Source.ToSszContainer();
            yield return item.Target.ToSszContainer();
        }
    }
}
