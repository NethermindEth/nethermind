// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Consensus.Ethash
{
    internal class HintBasedCache
    {
        private readonly Dictionary<Guid, HashSet<uint>> _epochsPerGuid = new();
        private readonly Dictionary<uint, int> _epochRefs = new();
        private readonly Dictionary<uint, Task<IEthashDataSet>> _cachedSets = new();
        private readonly Dictionary<uint, DataSetWithTime> _recent = new();

        private struct DataSetWithTime
        {
            public DataSetWithTime(DateTimeOffset timestamp, Task<IEthashDataSet> dataSet)
            {
                Timestamp = timestamp;
                DataSet = dataSet;
            }

            public DateTimeOffset Timestamp { get; set; }
            public Task<IEthashDataSet> DataSet { get; set; }
        }

        private int _cachedEpochsCount;

        public int CachedEpochsCount => _cachedEpochsCount;

        private readonly Func<uint, IEthashDataSet> _createDataSet;
        private readonly ILogger _logger;

        public HintBasedCache(Func<uint, IEthashDataSet> createDataSet, ILogManager logManager)
        {
            _createDataSet = createDataSet;
            _logger = logManager?.GetClassLogger<HintBasedCache>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Hint(Guid guid, long start, long end)
        {
            uint startEpoch = (uint)(start / Ethash.EpochLength);
            uint endEpoch = (uint)(end / Ethash.EpochLength);

            if (endEpoch - startEpoch > 10)
            {
                throw new InvalidOperationException("Hint too wide");
            }

            ref HashSet<uint>? value = ref CollectionsMarshal.GetValueRefOrAddDefault(_epochsPerGuid, guid, out bool exists);
            if (!exists)
            {
                value = new HashSet<uint>();
            }

            HashSet<uint> epochForGuid = value;
            uint currentMin = uint.MaxValue;
            uint currentMax = 0;
            foreach (uint alreadyCachedEpoch in epochForGuid.ToList())
            {
                if (alreadyCachedEpoch < currentMin)
                {
                    currentMin = alreadyCachedEpoch;
                }

                if (alreadyCachedEpoch > currentMax)
                {
                    currentMax = alreadyCachedEpoch;
                }

                if (alreadyCachedEpoch < startEpoch || alreadyCachedEpoch > endEpoch)
                {
                    epochForGuid.Remove(alreadyCachedEpoch);
                    if (!_epochRefs.TryGetValue(alreadyCachedEpoch, out var epochValue))
                    {
                        throw new InvalidAsynchronousStateException("Epoch ref missing");
                    }

                    _epochRefs[alreadyCachedEpoch] = epochValue - 1;
                    if (_epochRefs[alreadyCachedEpoch] == 0)
                    {
                        // _logger.Warn($"Removing data set for epoch {alreadyCachedEpoch}");
                        _cachedSets.Remove(alreadyCachedEpoch, out Task<IEthashDataSet> removed);
                        _recent[alreadyCachedEpoch] = new DataSetWithTime(DateTimeOffset.UtcNow, removed);
                        Interlocked.Decrement(ref _cachedEpochsCount);
                    }
                }
            }

            if (currentMin > startEpoch || currentMax < endEpoch)
            {
                for (long i = startEpoch; i <= endEpoch; i++)
                {
                    uint epoch = (uint)i;
                    if (epochForGuid.Add(epoch))
                    {
                        if (!_epochRefs.TryGetValue(epoch, out var epochValue))
                        {
                            epochValue = 0;
                            _epochRefs[epoch] = epochValue;
                        }

                        _epochRefs[epoch] = epochValue + 1;
                        if (_epochRefs[epoch] == 1)
                        {
                            // _logger.Warn($"Building data set for epoch {epoch}");
                            if (_recent.Remove(epoch, out DataSetWithTime reused))
                            {
                                _cachedSets[epoch] = reused.DataSet;
                            }
                            else
                            {
                                foreach (KeyValuePair<uint, DataSetWithTime> recent in _recent.ToList())
                                {
                                    if (recent.Value.Timestamp < DateTimeOffset.UtcNow.AddSeconds(-30))
                                    {
                                        _recent.Remove(recent.Key);
                                    }
                                }

                                _cachedSets[epoch] = Task<IEthashDataSet>.Run(() => _createDataSet(epoch));
                            }

                            Interlocked.Increment(ref _cachedEpochsCount);
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
