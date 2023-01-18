// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.TxPool;
using Nethermind.TxPool.Collections;

namespace Nethermind.Mev.Source
{

    public class BundlePool : IBundlePool, ISimulatedBundleSource, IDisposable
    {
        private readonly ITimestamper _timestamper;
        private readonly ITxValidator _txValidator;
        private readonly IMevConfig _mevConfig;
        private readonly IAccountStateProvider _stateProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IBundleSimulator _simulator;
        private readonly BundleSortedPool _bundles;
        private readonly ConcurrentDictionary<Address, MevBundle> _megabundles = new();
        private readonly ConcurrentDictionary<long, ConcurrentDictionary<(MevBundle Bundle, Keccak BlockHash), SimulatedMevBundleContext>> _simulatedBundles = new();
        private readonly ILogger _logger;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly HashSet<Address> _trustedRelays;

        private long HeadNumber => _blockTree.Head?.Number ?? 0;

        public event EventHandler<BundleEventArgs>? NewReceived;
        public event EventHandler<BundleEventArgs>? NewPending;

        public BundlePool(
            IBlockTree blockTree,
            IBundleSimulator simulator,
            ITimestamper timestamper,
            ITxValidator txValidator,
            ISpecProvider specProvider,
            IMevConfig mevConfig,
            IAccountStateProvider stateProvider,
            ILogManager logManager,
            IEthereumEcdsa ecdsa)
        {
            _timestamper = timestamper;
            _txValidator = txValidator;
            _mevConfig = mevConfig;
            _stateProvider = stateProvider;
            _specProvider = specProvider;
            _blockTree = blockTree;
            _simulator = simulator;
            _logger = logManager.GetClassLogger();
            _ecdsa = ecdsa;

            _trustedRelays = _mevConfig.GetTrustedRelayAddresses().ToHashSet();

            IComparer<MevBundle> comparer = CompareMevBundleByBlock.Default.ThenBy(CompareMevBundleByMinTimestamp.Default);
            _bundles = new BundleSortedPool(
                _mevConfig.BundlePoolSize,
                comparer,
                logManager);

            _bundles.Removed += OnBundleRemoved;
            _blockTree.NewHeadBlock += OnNewBlock;
        }

        public Task<IEnumerable<MevBundle>> GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token = default) =>
            Task.FromResult(GetBundles(parent.Number + 1, timestamp, token));

        public IEnumerable<MevBundle> GetBundles(long blockNumber, UInt256 timestamp, CancellationToken token = default) =>
            GetBundles(blockNumber, timestamp, timestamp, token);

        private IEnumerable<MevBundle> GetBundles(long blockNumber, UInt256 minTimestamp, UInt256 maxTimestamp, CancellationToken token = default)
        {
            if (_bundles.TryGetBucket(blockNumber, out MevBundle[] bundles))
            {
                foreach (MevBundle mevBundle in bundles)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    if (BundleInTimestampRange(mevBundle, minTimestamp, maxTimestamp))
                    {
                        yield return mevBundle;
                    }
                }
            }
        }

        public Task<IEnumerable<MevBundle>> GetMegabundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token = default) =>
            Task.FromResult(GetMegabundles(parent.Number + 1, timestamp, token));

        public IEnumerable<MevBundle> GetMegabundles(long blockNumber, UInt256 timestamp, CancellationToken token = default) =>
            GetMegabundles(blockNumber, timestamp, timestamp, token);

        private IEnumerable<MevBundle> GetMegabundles(long blockNumber, UInt256 minTimestamp, UInt256 maxTimestamp,
            CancellationToken token = default)
        {
            foreach (var bundle in _megabundles.Values)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                bool bundleIsInCurrentBlock = bundle.BlockNumber == blockNumber;
                if (BundleInTimestampRange(bundle, minTimestamp, maxTimestamp) && bundleIsInCurrentBlock)
                {
                    yield return bundle;
                }
            }
        }

        public bool AddBundle(MevBundle bundle)
        {
            Metrics.BundlesReceived++;
            BundleEventArgs bundleEventArgs = new(bundle);
            NewReceived?.Invoke(this, bundleEventArgs);

            if (ValidateBundle(bundle))
            {
                bool result = _bundles.TryInsert(bundle, bundle);

                if (result)
                {
                    Metrics.ValidBundlesReceived++;
                    NewPending?.Invoke(this, bundleEventArgs);
                    if (bundle.BlockNumber == HeadNumber + 1)
                    {
                        TrySimulateBundle(bundle);
                    }
                }

                return result;
            }

            return false;
        }

        public bool AddMegabundle(MevMegabundle megabundle)
        {
            Metrics.MegabundlesReceived++;
            BundleEventArgs bundleEventArgs = new(megabundle);
            NewReceived?.Invoke(this, bundleEventArgs);

            if (ValidateBundle(megabundle))
            {
                Address relayAddress = megabundle.RelayAddress = _ecdsa.RecoverAddress(megabundle.RelaySignature!, megabundle.Hash)!;
                if (IsTrustedRelay(relayAddress))
                {
                    Metrics.ValidMegabundlesReceived++;
                    NewPending?.Invoke(this, bundleEventArgs);

                    // add megabundle from trusted relay into dictionary
                    // stop and remove simulation if relay has previously sent a megabundle
                    _megabundles.AddOrUpdate(relayAddress,
                        _ => megabundle,
                        (_, previousBundle) =>
                        {
                            RemoveSimulation(previousBundle);
                            return megabundle;
                        });

                    if (megabundle.BlockNumber == HeadNumber + 1)
                    {
                        TrySimulateBundle(megabundle);
                    }

                    return true;
                }
            }

            return false;
        }

        private bool BundleInTimestampRange(MevBundle bundle, UInt256 minTimestamp, UInt256 maxTimestamp)
        {
            bool bundleIsInFuture = bundle.MinTimestamp != UInt256.Zero && minTimestamp < bundle.MinTimestamp;
            bool bundleIsTooOld = bundle.MaxTimestamp != UInt256.Zero && maxTimestamp > bundle.MaxTimestamp;
            return !bundleIsInFuture && !bundleIsTooOld;
        }

        private bool IsTrustedRelay(Address relayAddress) => _trustedRelays.Contains(relayAddress);

        private bool ValidateBundle(MevBundle bundle)
        {
            if (HeadNumber >= bundle.BlockNumber)
            {
                return false;
            }

            if (bundle.Transactions.Count == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Bundle rejected, because it doesn't contain transactions.");
                return false;
            }

            ulong currentTimestamp = _timestamper.UnixTime.Seconds;

            if (bundle.MaxTimestamp < bundle.MinTimestamp)
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Bundle rejected, because {nameof(bundle.MaxTimestamp)} {bundle.MaxTimestamp} is < {nameof(bundle.MinTimestamp)} {bundle.MinTimestamp}.");
                return false;
            }
            else if (bundle.MaxTimestamp != 0 && bundle.MaxTimestamp < currentTimestamp)
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Bundle rejected, because {nameof(bundle.MaxTimestamp)} {bundle.MaxTimestamp} is < current {currentTimestamp}.");
                return false;
            }
            else if (bundle.MinTimestamp != 0 && bundle.MinTimestamp > currentTimestamp + _mevConfig.BundleHorizon)
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Bundle rejected, because {nameof(bundle.MinTimestamp)} {bundle.MinTimestamp} is further into the future than accepted horizon {_mevConfig.BundleHorizon}.");
                return false;
            }

            IReleaseSpec spec = _specProvider.GetSpec(bundle.BlockNumber, currentTimestamp);
            for (int i = 0; i < bundle.Transactions.Count; i++)
            {
                BundleTransaction tx = bundle.Transactions[i];
                if (!tx.CanRevert)
                {
                    if (!_txValidator.IsWellFormed(tx, spec))
                    {
                        return false;
                    }

                    if (tx.SenderAddress is null)
                    {
                        tx.SenderAddress = _ecdsa.RecoverAddress(tx);
                        if (tx.SenderAddress is null)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Bundle rejected, because transaction {tx.Hash} has no sender.");
                            return false;
                        }
                    }

                    if (_stateProvider.IsInvalidContractSender(spec, tx.SenderAddress!))
                    {
                        if (_logger.IsDebug) _logger.Debug($"Bundle rejected, because transaction {tx.Hash} sender {tx.SenderAddress} is contract.");
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TrySimulateBundle(MevBundle bundle)
        {
            var head = _blockTree.Head;
            if (head is not null)
            {
                if (head.Number + 1 == bundle.BlockNumber)
                {
                    SimulateBundle(bundle, head.Header);
                    return true;
                }
            }

            return false;
        }

        protected virtual SimulatedMevBundleContext? SimulateBundle(MevBundle bundle, BlockHeader parent)
        {
            SimulatedMevBundleContext? context = null;
            (MevBundle, Keccak) key = (bundle, parent.Hash!);

            Metrics.BundlesSimulated++;

            SimulatedMevBundleContext CreateContext()
            {
                CancellationTokenSource cancellationTokenSource = new();
                Task<SimulatedMevBundle> simulateTask = _simulator.Simulate(bundle, parent, cancellationTokenSource.Token);
                simulateTask.ContinueWith(TryRemoveFailedSimulatedBundle, cancellationTokenSource.Token);
                return context = new(simulateTask, cancellationTokenSource);
            }

            ConcurrentDictionary<(MevBundle, Keccak), SimulatedMevBundleContext> AddContext(ConcurrentDictionary<(MevBundle, Keccak), SimulatedMevBundleContext> d)
            {
                d.AddOrUpdate(key,
                    _ => CreateContext(),
                    (_, c) => c);
                return d;
            }

            _simulatedBundles.AddOrUpdate(parent.Number,
                    _ =>
                    {
                        ConcurrentDictionary<(MevBundle, Keccak), SimulatedMevBundleContext> d = new();
                        return AddContext(d);
                    },
                    (_, d) => AddContext(d));

            return context;
        }

        private void TryRemoveFailedSimulatedBundle(Task<SimulatedMevBundle> simulateTask)
        {
            if (simulateTask.IsCompletedSuccessfully)
            {
                SimulatedMevBundle simulatedMevBundle = simulateTask.Result;
                if (!simulatedMevBundle.Success)
                {
                    _bundles.TryRemove(simulatedMevBundle.Bundle);
                    RemoveSimulation(simulatedMevBundle.Bundle);
                }
            }
        }

        private void OnNewBlock(object? sender, BlockEventArgs e)
        {
            long blockNumber = e.Block!.Number;
            RemoveBundlesUpToBlock(blockNumber);

            Task.Run(() =>
            {
                UInt256 timestamp = _timestamper.UnixTime.Seconds;
                IEnumerable<MevBundle> bundles = GetBundles(e.Block.Number + 1, UInt256.MaxValue, timestamp);
                IEnumerable<MevBundle> megabundles = GetMegabundles(e.Block.Number + 1, UInt256.MaxValue, timestamp);
                IEnumerable<MevBundle> allBundles = bundles.Concat(megabundles);
                foreach (MevBundle bundle in allBundles)
                {
                    SimulateBundle(bundle, e.Block.Header);
                }
            });
        }

        private void RemoveBundlesUpToBlock(long blockNumber)
        {
            void StopSimulations(IEnumerable<SimulatedMevBundleContext> simulations)
            {
                foreach (SimulatedMevBundleContext simulation in simulations)
                {
                    StopSimulation(simulation);
                }
            }

            IDictionary<long, MevBundle[]> bundlesToRemove = _bundles.GetBucketSnapshot(b => b <= blockNumber);

            foreach (KeyValuePair<long, MevBundle[]> bundleBucket in bundlesToRemove)
            {
                if (_simulatedBundles.TryRemove(bundleBucket.Key, out var simulations))
                {
                    StopSimulations(simulations.Values);
                }

                foreach (MevBundle mevBundle in bundleBucket.Value)
                {
                    _bundles.TryRemove(mevBundle);
                }
            }

            var simulatedBundlesToRemove = _simulatedBundles.Where(b => b.Key <= blockNumber).ToList();
            foreach (KeyValuePair<long, ConcurrentDictionary<(MevBundle, Keccak), SimulatedMevBundleContext>> simulatedBundleBucket in simulatedBundlesToRemove)
            {
                StopSimulations(simulatedBundleBucket.Value.Values);
                _simulatedBundles.TryRemove(simulatedBundleBucket.Key, out _);
            }

            IEnumerable<Address> megabundleKeysToRemove = _megabundles
                .Where(m => m.Value.BlockNumber <= blockNumber)
                .Select(m => m.Key);
            foreach (Address address in megabundleKeysToRemove)
            {
                if (_megabundles.TryRemove(address, out MevBundle? megabundle))
                {
                    RemoveSimulation(megabundle);
                }
            }
        }

        private void OnBundleRemoved(object? sender, SortedPool<MevBundle, MevBundle, long>.SortedPoolRemovedEventArgs e)
        {
            if (e.Evicted)
            {
                RemoveSimulation(e.Key);
            }
        }

        private void RemoveSimulation(MevBundle bundle)
        {
            if (_simulatedBundles.TryGetValue(bundle.BlockNumber, out ConcurrentDictionary<(MevBundle Bundle, Keccak BlockHash), SimulatedMevBundleContext>? simulations))
            {
                IEnumerable<(MevBundle Bundle, Keccak BlockHash)> keys = simulations.Keys.Where(k => Equals(k.Bundle, bundle));

                foreach ((MevBundle Bundle, Keccak BlockHash) key in keys)
                {
                    if (simulations.TryRemove(key, out var simulation))
                    {
                        StopSimulation(simulation);
                    }
                }

                if (simulations.IsEmpty)
                {
                    _simulatedBundles.Remove(bundle.BlockNumber, out _);
                }
            }
        }

        private void StopSimulation(SimulatedMevBundleContext simulation)
        {
            if (!simulation.Task.IsCompleted)
            {
                simulation.CancellationTokenSource.Cancel();
            }
        }

        private async Task<IEnumerable<SimulatedMevBundle>> GetSimulatedBundles(HashSet<MevBundle> bundles, BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token)
        {
            if (_simulatedBundles.TryGetValue(parent.Number, out ConcurrentDictionary<(MevBundle Bundle, Keccak BlockHash), SimulatedMevBundleContext>? simulatedBundlesForBlock))
            {
                IEnumerable<Task<SimulatedMevBundle>> resultTasks = simulatedBundlesForBlock
                    .Where(b => b.Key.BlockHash == parent.Hash)
                    .Where(b => bundles.Contains(b.Key.Bundle))
                    .Select(b => b.Value.Task)
                    .ToArray();

                await Task.WhenAny(Task.WhenAll(resultTasks), token.AsTask());

                IEnumerable<SimulatedMevBundle> res = resultTasks
                    .Where(t => t.IsCompletedSuccessfully)
                    .Select(t => t.Result)
                    .Where(t => t.Success)
                    .Where(s => s.GasUsed <= gasLimit);

                return res;
            }
            return (Enumerable.Empty<SimulatedMevBundle>());
        }

        async Task<IEnumerable<SimulatedMevBundle>> ISimulatedBundleSource.GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token)
        {
            HashSet<MevBundle> bundles = (await GetBundles(parent, timestamp, gasLimit, token)).ToHashSet();
            return await GetSimulatedBundles(bundles, parent, timestamp, gasLimit, token);
        }

        async Task<IEnumerable<SimulatedMevBundle>> ISimulatedBundleSource.GetMegabundles(BlockHeader parent, UInt256 timestamp,
            long gasLimit, CancellationToken token)
        {
            HashSet<MevBundle> bundles = (await GetMegabundles(parent, timestamp, gasLimit, token)).ToHashSet();
            return await GetSimulatedBundles(bundles, parent, timestamp, gasLimit, token);
        }

        public void Dispose()
        {
            _blockTree.NewHeadBlock -= OnNewBlock;
            _bundles.Removed -= OnBundleRemoved;
        }

        protected class SimulatedMevBundleContext : IDisposable
        {
            public SimulatedMevBundleContext(Task<SimulatedMevBundle> task, CancellationTokenSource cancellationTokenSource)
            {
                Task = task;
                CancellationTokenSource = cancellationTokenSource;
            }

            public CancellationTokenSource CancellationTokenSource { get; }
            public Task<SimulatedMevBundle> Task { get; }

            public void Dispose()
            {
                CancellationTokenSource.Dispose();
            }
        }
    }
}
