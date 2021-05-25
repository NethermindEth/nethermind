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
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.TxPool.Collections;
using Org.BouncyCastle.Security;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Mev.Source
{

    public class BundlePool : IBundlePool, ISimulatedBundleSource, IDisposable
    {
        private readonly IBlockFinalizationManager? _finalizationManager;
        private readonly ITimestamper _timestamper;
        private readonly IMevConfig _mevConfig;
        private readonly IBlockTree _blockTree;
        private readonly IBundleSimulator _simulator;
        private readonly SortedPool<MevBundle, BundleWithHashes, long> _bundles2;
        private readonly IDictionary<MevBundle, BundleWithHashes>? _cachemap;
        private readonly ConcurrentDictionary<Keccak, ConcurrentDictionary<MevBundle, SimulatedMevBundleContext>> _simulatedBundles = new();
        private readonly ILogger _logger;
        private readonly CompareMevBundlesByBlock _compareByBlock;
        public BundlePool(
            IBlockTree blockTree, 
            IBundleSimulator simulator,
            IBlockFinalizationManager? finalizationManager,
            ITimestamper timestamper,
            IMevConfig mevConfig,
            ILogManager logManager)
        {
            _finalizationManager = finalizationManager;
            _timestamper = timestamper;
            _mevConfig = mevConfig;
            _blockTree = blockTree;
            _simulator = simulator;
            _blockTree.NewSuggestedBlock += OnNewSuggestedBlock;
            _logger = logManager.GetClassLogger();

            _compareByBlock = new CompareMevBundlesByBlock {BestBlockNumber = blockTree.BestSuggestedHeader?.Number ?? 0};
            _bundles2 = new BundleSortedPool(
                _mevConfig.BundlePoolSize,
                _compareByBlock.ThenBy(CompareMevBundlesByMinTimestamp.Default),
                logManager ); 
            _cachemap = _bundles2.GetCacheMap();
            
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
            /*
            int CompareBundles(MevBundle searchedBundle, KeyValuePair<MevBundle, ConcurrentBag<Keccak>> potentialBundle)
            {
                return searchedBundle.BlockNumber <= potentialBundle.Key.BlockNumber ? -1 : 1;
            }*/

            lock (_bundles2)
            {
                MevBundle searchedBundle = MevBundle.Empty(blockNumber, minTimestamp, maxTimestamp);
                bool inBundle = _bundles2.TryGetValue(searchedBundle, out BundleWithHashes value); //does it matter that searchedBundle is same?
                if (inBundle)
                {
                    foreach (KeyValuePair<MevBundle, BundleWithHashes> kvp in _bundles2.GetCacheMap()) //is the complement of i to prevent us from checking if i is not in this list?, but ~~of a number is the same number...
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        MevBundle mevBundle = kvp.Key;
                        if (mevBundle.BlockNumber == searchedBundle.BlockNumber)
                        {
                            bool bundleIsInFuture = mevBundle.MinTimestamp != UInt256.Zero &&
                                                    searchedBundle.MinTimestamp < mevBundle.MinTimestamp;
                            bool bundleIsTooOld = mevBundle.MaxTimestamp != UInt256.Zero &&
                                                  searchedBundle.MaxTimestamp > mevBundle.MaxTimestamp;
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
        }

        public bool AddBundle(MevBundle bundle)
        {
            if (ValidateBundle(bundle))
            {
                bool result;

                lock (_bundles2)
                {
                    result = _bundles2.TryInsert(bundle, new BundleWithHashes(bundle));
                }

                if (result)
                {
                    SimulateBundle(bundle);
                }

                return result;
            }

            return false;
        }

        private bool ValidateBundle(MevBundle bundle)
        {
            if (_finalizationManager?.IsFinalized(bundle.BlockNumber) == true)
            {
                return false;
            }

            UInt256 currentTimestamp = _timestamper.UnixTime.Seconds;

            if (bundle.MaxTimestamp < bundle.MinTimestamp)
            {
                if (_logger.IsDebug) _logger.Debug($"Bundle rejected, because {nameof(bundle.MaxTimestamp)} {bundle.MaxTimestamp} is < {nameof(bundle.MinTimestamp)} {bundle.MinTimestamp}.");
                return false;
            }
            else if (bundle.MaxTimestamp != 0 && bundle.MaxTimestamp < currentTimestamp)
            {
                if (_logger.IsDebug) _logger.Debug($"Bundle rejected, because {nameof(bundle.MaxTimestamp)} {bundle.MaxTimestamp} is < current {currentTimestamp}.");
                return false;
            }
            else if (bundle.MinTimestamp != 0 && bundle.MinTimestamp > currentTimestamp + _mevConfig.BundleHorizon)
            {
                if (_logger.IsDebug) _logger.Debug($"Bundle rejected, because {nameof(bundle.MinTimestamp)} {bundle.MaxTimestamp} is further into the future than accepted horizon {_mevConfig.BundleHorizon}.");
                return false;
            }

            return true;
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
            //do we still need blockdictionary?
            Keccak parentHash = parent.Hash!;
            ConcurrentDictionary<MevBundle, SimulatedMevBundleContext> blockDictionary = 
                _simulatedBundles.GetOrAdd(parentHash, _ => new ConcurrentDictionary<MevBundle, SimulatedMevBundleContext>());

            SimulatedMevBundleContext context = new();
            if (blockDictionary.TryAdd(bundle, context))
            {
                context.Task = _simulator.Simulate(bundle, parent, context.CancellationTokenSource.Token);
            }
            
            lock (_bundles2)
            {
                if (_cachemap!.ContainsKey(bundle))
                {
                     _cachemap[bundle].BlockHashes.Add(parentHash);
                }
                else
                {
                    BundleWithHashes newBundleWithHashes = new BundleWithHashes(bundle);
                    newBundleWithHashes.BlockHashes.Add(parentHash);
                    _cachemap[bundle] = newBundleWithHashes;
                }
            }
        }
        
        private void OnNewSuggestedBlock(object? sender, BlockEventArgs e)
        {
            long blockNumber = e.Block!.Number;
            ResortBundlesByBlock(blockNumber);

            if (_finalizationManager?.IsFinalized(blockNumber) != true)
            {
                Task.Run(() =>
                {
                    IEnumerable<MevBundle> bundles = GetBundles(e.Block.Number + 1, UInt256.MaxValue, UInt256.Zero);
                    foreach (MevBundle bundle in bundles)
                    {
                        SimulateBundle(bundle, e.Block.Header);
                    }
                });
            }
        }

        private void ResortBundlesByBlock(long newBlockNumber)
        {
            IEnumerable<long> Range(long start, long count)
            {
                for (long i = start; i < start + count; i++)
                {
                    yield return i;
                }
            }
            
            long previousBestSuggested = _compareByBlock.BestBlockNumber;
            long fromBlockNumber = Math.Min(newBlockNumber, previousBestSuggested);
            long blockDelta = Math.Abs(newBlockNumber - previousBestSuggested);
            _bundles2.NotifyChange(Range(fromBlockNumber, blockDelta), () => _compareByBlock.BestBlockNumber = newBlockNumber);
        }

        private void OnBlocksFinalized(object? sender, FinalizeEventArgs e)
        {
            long maxFinalizedBlockNumber = e.FinalizedBlocks.Select(b => b.Number).Max();
            int count = _bundles2.Count;
            int capacity = _mevConfig.BundlePoolSize;
            lock (_bundles2)
            {
                if (_bundles2.Count > capacity) //remove if bundles more than capacity
                {
                    foreach (KeyValuePair<MevBundle, BundleWithHashes> kvp in _bundles2.GetCacheMap())
                    {
                        MevBundle? bundleCpy = kvp.Key;
                        _bundles2.TryRemove(kvp.Key, out BundleWithHashes? bundleHash); //want to make this same as Key, does this need to be out?
                        _cachemap!.Remove(kvp.Key);
                        if (_bundles2.Count <= capacity)
                        {
                            break;
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
                    .Where(t => t.Success)
                    .Where(s => s.GasUsed <= gasLimit);
            }
            else
            {
                return Enumerable.Empty<SimulatedMevBundle>();
            }
        }

        public void Dispose() //is this supposed to be public and shifted right?
        {
            _blockTree.NewSuggestedBlock -= OnNewSuggestedBlock;
            
            if (_finalizationManager != null)
            {
                _finalizationManager.BlocksFinalized -= OnBlocksFinalized;
            }
        }
        
        private class MevBundleComparer : IComparer<MevBundle>
        {
            public static readonly MevBundleComparer Default = new();
            
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
                int maxTimestampComparison = y.MaxTimestamp.CompareTo(x.MaxTimestamp);
                if (maxTimestampComparison != 0) return maxTimestampComparison;

                for (int i = 0; i < Math.Max(x.Transactions.Count, y.Transactions.Count); i++)
                {
                    Keccak? xHash = x.Transactions.Count > i ? x.Transactions[i].Hash : null;
                    if (xHash is null) return -1;
                    
                    Keccak? yHash = y.Transactions.Count > i ? y.Transactions[i].Hash : null;
                    if (yHash is null) return 1;

                    int hashComparision = xHash.CompareTo(yHash);
                    if (hashComparision != 0) return hashComparision;
                }

                return 0;
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
