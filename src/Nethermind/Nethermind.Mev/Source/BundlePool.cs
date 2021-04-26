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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public class BundlePool : IBundlePool
    {
        private readonly SortedRealList<MevBundle, MevBundle> _bundles = new(MevBundleComparer.Default);
        
        public Task<IEnumerable<MevBundle>> GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit) => 
            Task.FromResult(GetBundles(parent.Number + 1, timestamp));

        public IEnumerable<MevBundle> GetBundles(long blockNumber, UInt256 timestamp)
        {
            int CompareBundles(MevBundle searchedBundle, KeyValuePair<MevBundle, MevBundle> potentialBundle) =>
                searchedBundle.BlockNumber.CompareTo(potentialBundle.Key.BlockNumber);
            
            lock (_bundles)
            {
                MevBundle searchedBundle = MevBundle.Empty(blockNumber, timestamp);
                int i = _bundles.BinarySearch(searchedBundle, CompareBundles);
                for (int j = i >= 0 ? i : ~i; j < _bundles.Count; j++)
                {
                    MevBundle mevBundle = _bundles[j].Key;
                    if (mevBundle.BlockNumber == searchedBundle.BlockNumber)
                    {
                        bool bundleIsInFuture = mevBundle.MinTimestamp != UInt256.Zero && searchedBundle.MinTimestamp < mevBundle.MinTimestamp;
                        bool bundleIsTooOld = mevBundle.MaxTimestamp != UInt256.Zero && searchedBundle.MaxTimestamp > mevBundle.MaxTimestamp;
                        if (!bundleIsInFuture && !bundleIsTooOld)
                        {
                            yield return mevBundle;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        public bool AddBundle(MevBundle bundle)
        {
            lock (_bundles)
            {
                return _bundles.TryAdd(bundle, bundle);
            }
        }

        private class MevBundleComparer : IComparer<MevBundle>
        {
            public static readonly MevBundleComparer Default = new MevBundleComparer();
            
            public int Compare(MevBundle? x, MevBundle? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (ReferenceEquals(null, y)) return 1;
                if (ReferenceEquals(null, x)) return -1;
            
                // block number increasing
                int blockNumberComparison = x.BlockNumber.CompareTo(y.BlockNumber);
                if (blockNumberComparison != 0) return blockNumberComparison;
            
                // min timestamp increasing
                int minTimestampComparison = x.MinTimestamp.CompareTo(y.MinTimestamp);
                if (minTimestampComparison != 0) return minTimestampComparison;
            
                // max timestamp decreasing
                return y.MaxTimestamp.CompareTo(x.MaxTimestamp);
            }
        }
    }
}
