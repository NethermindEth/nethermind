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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;

namespace Nethermind.Mev.Source
{
    public class BundlePool : IBundlePool, ISimulatedBundleSource, IDisposable
    {
        private readonly IBlockFinalizationManager? _finalizationManager;
        private readonly IBlockTree _blockTree;
        private readonly IBundleSimulator _simulator;
        private readonly SortedRealList<MevBundle, ConcurrentBag<Keccak>> _bundles = new(MevBundleComparer.Default);
        private readonly ConcurrentDictionary<Keccak, ConcurrentDictionary<MevBundle, SimulatedMevBundleContext>> _simulatedBundles = new();

        public BundlePool(IBlockTree blockTree, IBundleSimulator simulator, IBlockFinalizationManager? finalizationManager)
        {
            _finalizationManager = finalizationManager;
            _blockTree = blockTree;
            _simulator = simulator;
            _blockTree.NewSuggestedBlock += OnNewSuggestedBlock;
            
            if (_finalizationManager != null)
            {
                _finalizationManager.BlocksFinalized += OnBlocksFinalized;
            }
        }

        public Task<IEnumerable<MevBundle>> GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token = default) => 
            Task.FromResult(GetBundles(parent.Number + 1, timestamp, token));

        public IEnumerable<MevBundle> GetBundles(long blockNumber, UInt256 timestamp, CancellationToken token = default) => 
            GetBundles(blockNumber, timestamp, timestamp, token);

        private IEnumerable<MevBundle> GetBundles(long blockNumber, UInt256 minTimestamp, UInt256 maxTimestamp, CancellationToken token = default)
        {
            int CompareBundles(MevBundle searchedBundle, KeyValuePair<MevBundle, ConcurrentBag<Keccak>> potentialBundle) =>
                searchedBundle.BlockNumber.CompareTo(potentialBundle.Key.BlockNumber);
            
            lock (_bundles)
            {
                MevBundle searchedBundle = MevBundle.Empty(blockNumber, minTimestamp, maxTimestamp);
                int i = _bundles.BinarySearch(searchedBundle, CompareBundles);
                for (int j = i >= 0 ? i : ~i; j < _bundles.Count; j++)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    
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
            if (_finalizationManager?.IsFinalized(bundle.BlockNumber) == true)
            {
                return false;
            }

            bool result;
            
            lock (_bundles)
            {
                result = _bundles.TryAdd(bundle, new ConcurrentBag<Keccak>());
            }
            
            if (result)
            {
                SimulateBundle(bundle);
            }

            return result;
        }

        private void SimulateBundle(MevBundle bundle)
        {
            ChainLevelInfo? level = _blockTree.FindLevel(bundle.BlockNumber - 1);
            if (level is not null)
            {
                for (int i = 0; i < level.BlockInfos.Length; i++)
                {
                    BlockHeader? header = _blockTree.FindHeader(level.BlockInfos[i].BlockHash, BlockTreeLookupOptions.None);
                    if (header is not null)
                    {
                        SimulateBundle(bundle, header);
                    }
                }
            }
        }

        private void SimulateBundle(MevBundle bundle, BlockHeader parent)
        {
            Keccak parentHash = parent.Hash!;
            ConcurrentDictionary<MevBundle, SimulatedMevBundleContext> blockDictionary = 
                _simulatedBundles.GetOrAdd(parentHash, _ => new ConcurrentDictionary<MevBundle, SimulatedMevBundleContext>());

            SimulatedMevBundleContext context = new();
            if (blockDictionary.TryAdd(bundle, context))
            {
                context.Task = _simulator.Simulate(bundle, parent, context.CancellationTokenSource.Token);
            }

            ConcurrentBag<Keccak> blocksBag;
            lock (_bundles)
            {
                blocksBag = _bundles[bundle];
            }
            blocksBag.Add(parentHash);
        }
        
        private void OnNewSuggestedBlock(object? sender, BlockEventArgs e)
        {
            long blockNumber = e.Block!.Number;
            if (_finalizationManager?.IsFinalized(blockNumber) != true)
            {
                IEnumerable<MevBundle> bundles = GetBundles(e.Block.Number + 1, UInt256.MaxValue, UInt256.Zero);
                foreach (MevBundle bundle in bundles)
                {
                    SimulateBundle(bundle, e.Block.Header);
                }
            }
        }

        private void OnBlocksFinalized(object? sender, FinalizeEventArgs e)
        {
            long maxFinalizedBlockNumber = e.FinalizedBlocks.Select(b => b.Number).Max();
            if (_bundles.Count > 0)
            {
                lock (_bundles)
                {
                    if (_bundles.Count > 0)
                    {
                        MevBundle bundle = _bundles.Keys[0];
                        while (bundle.BlockNumber <= maxFinalizedBlockNumber)
                        {
                            ConcurrentBag<Keccak> blocksBag = _bundles.Values[0];
                            foreach (Keccak blockHash in blocksBag)
                            {
                                if (_simulatedBundles.TryGetValue(blockHash, out ConcurrentDictionary<MevBundle, SimulatedMevBundleContext>? bundleDictionary))
                                {
                                    if (bundleDictionary.TryRemove(bundle, out SimulatedMevBundleContext? context))
                                    {
                                        context.CancellationTokenSource.Cancel();
                                        context.Dispose();
                                    }

                                    if (bundleDictionary.Count == 0)
                                    {
                                        _simulatedBundles.TryRemove(blockHash, out _);
                                    }
                                }
                            }

                            _bundles.RemoveAt(0);
                            
                            if (_bundles.Count > 0)
                            {
                                bundle = _bundles.Keys[0];
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }
        
        async Task<IEnumerable<SimulatedMevBundle>> ISimulatedBundleSource.GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token)
        {
            HashSet<MevBundle> bundles = (await GetBundles(parent, timestamp, gasLimit, token)).ToHashSet();
            
            Keccak parentHash = parent.Hash!;
            if (_simulatedBundles.TryGetValue(parentHash, out ConcurrentDictionary<MevBundle, SimulatedMevBundleContext>? simulatedBundlesForBlock))
            {
                IEnumerable<Task<SimulatedMevBundle>> resultTasks = simulatedBundlesForBlock
                    .Where(b => bundles.Contains(b.Key))
                    .Select(b => b.Value.Task)
                    .ToArray();

                await Task.WhenAny(Task.WhenAll(resultTasks), token.AsTask());

                return resultTasks
                    .Where(t => t.IsCompletedSuccessfully)
                    .Select(t => t.Result)
                    .Where(s => s.GasUsed <= gasLimit);
            }
            else
            {
                return Enumerable.Empty<SimulatedMevBundle>();
            }
        }
        
        public void Dispose()
        {
            _blockTree.NewSuggestedBlock -= OnNewSuggestedBlock;
            
            if (_finalizationManager != null)
            {
                _finalizationManager.BlocksFinalized -= OnBlocksFinalized;
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
        
        private class SimulatedMevBundleContext : IDisposable
        {
            public CancellationTokenSource CancellationTokenSource { get; } = new();
            public Task<SimulatedMevBundle> Task { get; set; } = null!;

            public void Dispose()
            {
                CancellationTokenSource.Dispose();
            }
        }
    }
}
