//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Collections.Generic;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public class CompareMevBundlesByBlock : IComparer<BundleWithHashes>
    {
        public long BestBlockNumber { get; set; }
        
        public int Compare(BundleWithHashes? x, BundleWithHashes? y)
        {
            if (ReferenceEquals(x, y)) return 0; 
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            MevBundle x1 = x.Bundle;
            MevBundle y1 = y.Bundle;

            if (x1.BlockNumber == y1.BlockNumber)
            {
                return 0;
            }
            else if (x1.BlockNumber > BestBlockNumber && y1.BlockNumber > BestBlockNumber)
            {
                return x1.BlockNumber.CompareTo(y1.BlockNumber);
            }
            else //if head is 5, and we have 8 and 4, we want to keep it that way; and if we have 4 and 3 we also want to keep it that way
            {
                return y1.BlockNumber.CompareTo(x1.BlockNumber);
            }
        }
    }
}
