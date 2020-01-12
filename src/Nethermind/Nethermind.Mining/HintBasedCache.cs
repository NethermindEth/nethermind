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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Mining
{
    public class HintBasedCache
    {
        private ConcurrentDictionary<Guid, HashSet<uint>> _epochsPerGuid = new ConcurrentDictionary<Guid, HashSet<uint>>();
        private ConcurrentDictionary<uint, int> _epochRefs = new ConcurrentDictionary<uint, int>();
        private ConcurrentDictionary<uint, Task<IEthashDataSet>> _cachedSets = new ConcurrentDictionary<uint, Task<IEthashDataSet>>();

        public int CachedEpochsCount { get; set; }

        private readonly Func<uint, IEthashDataSet> _createDataSet;

        public HintBasedCache(Func<uint, IEthashDataSet> createDataSet)
        {
            _createDataSet = createDataSet;
        }

        public void Hint(Guid guid, long start, long end)
        {
            uint startEpoch = (uint)(start / Ethash.EpochLength);
            uint endEpoch = (uint)(end / Ethash.EpochLength);

            if (endEpoch - startEpoch > 10)
            {
                throw new InvalidOperationException("Hint too wide");
            }
            
            HashSet<uint> epochForGuid = _epochsPerGuid.GetOrAdd(guid, new HashSet<uint>());
            lock (epochForGuid)
            {
                if (epochForGuid.Count > 0)
                {
                    foreach (uint cachedEpoch in epochForGuid.ToList())
                    {
                        if (cachedEpoch < startEpoch || cachedEpoch > endEpoch)
                        {
                            bool shouldRemove = false;
                            epochForGuid.Remove(cachedEpoch);
                            lock (_epochRefs)
                            {
                                _epochRefs[cachedEpoch] = _epochRefs[cachedEpoch] - 1;
                                if (_epochRefs[cachedEpoch] == 0)
                                {
                                    shouldRemove = true;
                                }
                            }

                            if (shouldRemove)
                            {
                                _cachedSets.Remove(cachedEpoch, out _);
                                CachedEpochsCount--;
                            }
                        }
                    }
                }

                for (long i = startEpoch; i <= endEpoch; i++)
                {
                    uint epoch = (uint) i;
                    if (!epochForGuid.Contains(epoch))
                    {
                        bool shouldAdd = false;
                        epochForGuid.Add(epoch);
                        lock (_epochRefs)
                        {
                            if (!_epochRefs.TryGetValue(epoch, out int refCount))
                            {
                                shouldAdd = true;
                            }

                            _epochRefs[epoch] = refCount + 1;
                        }

                        if (shouldAdd)
                        {
                            _cachedSets[epoch] = Task<IEthashDataSet>.Run(() => _createDataSet(epoch));
                            CachedEpochsCount++;
                        }
                    }
                }
            }
        }

        public IEthashDataSet Get(uint epoch)
        {
            _cachedSets.TryGetValue(epoch, out Task<IEthashDataSet> dataSetTask);
            return dataSetTask?.Result;
        }
    }
}