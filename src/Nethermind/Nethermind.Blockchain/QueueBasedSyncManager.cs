/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class QueueBasedSyncManager : ISynchronizationManager
    {
        private int _currentBatchSize = 256;
        public const int MinBatchSize = 8;
        public const int MaxBatchSize = 512;
        public const int MaxReorganizationLength = 2 * MaxBatchSize;
        private int _sinceLastTimeout;
        private UInt256 _lastSyncNumber = UInt256.Zero;

        private readonly ILogger _logger;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly IPerfService _perfService;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ConcurrentDictionary<PublicKey, PeerInfo> _peers = new ConcurrentDictionary<PublicKey, PeerInfo>();
        private readonly ConcurrentDictionary<PublicKey, CancellationTokenSource> _initCancelTokens = new ConcurrentDictionary<PublicKey, CancellationTokenSource>();

        private readonly ITransactionValidator _transactionValidator;
        private readonly IDb _stateDb;
        private readonly IBlockchainConfig _blockchainConfig;
        private readonly INodeStatsManager _stats;
        private readonly IBlockTree _blockTree;

        private bool _isInitialized;

        private PeerInfo _currentSyncingPeerInfo;
        private System.Timers.Timer _syncTimer;

        private CancellationTokenSource _peerSyncCancellationTokenSource;
        private bool _requestedSyncCancelDueToBetterPeer;
        private CancellationTokenSource _syncLoopCancelTokenSource = new CancellationTokenSource();
        private int _lastSyncPeersCount;

        private readonly BlockingCollection<PeerInfo> _peerRefreshQueue = new BlockingCollection<PeerInfo>();
        private readonly ManualResetEventSlim _syncRequested = new ManualResetEventSlim(false);

        public QueueBasedSyncManager(
            IDb stateDb,
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ITransactionValidator transactionValidator,
            ILogManager logManager,
            IBlockchainConfig blockchainConfig,
            INodeStatsManager nodeStatsManager,
            IPerfService perfService,
            IReceiptStorage receiptStorage)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _blockchainConfig = blockchainConfig ?? throw new ArgumentNullException(nameof(blockchainConfig));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _receiptStorage = receiptStorage;

            _transactionValidator = transactionValidator ?? throw new ArgumentNullException(nameof(transactionValidator));

            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));

            if (_logger.IsDebug && Head != null) _logger.Debug($"Initialized SynchronizationManager with head block {Head.ToString(BlockHeader.Format.Short)}");
        }

        private async Task RunRefreshPeerLoop()
        {
            try
            {
                foreach (PeerInfo peerInfo in _peerRefreshQueue.GetConsumingEnumerable(_syncLoopCancelTokenSource.Token))
                {
                    ISynchronizationPeer peer = peerInfo.Peer;
                    var initCancelSource = _initCancelTokens[peer.Node.Id] = new CancellationTokenSource();
                    var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(initCancelSource.Token, _syncLoopCancelTokenSource.Token);
                    await RefreshPeerInfo(peer, linkedSource.Token).ContinueWith(t =>
                    {
                        _initCancelTokens.TryRemove(peer.Node.Id, out _);
                        if (t.IsFaulted)
                        {
                            if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                            {
                                if (_logger.IsTrace) _logger.Trace($"AddPeer failed due to timeout: {t.Exception.Message}");
                            }
                            else if (_logger.IsDebug) _logger.Debug($"AddPeer failed {t.Exception}");
                        }
                        else if (t.IsCanceled)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Init peer info canceled: {peer.Node:s}");
                        }
                        else
                        {
                            CancelCurrentPeerSyncIfWorse(peerInfo, ComparedPeerType.New);
                            if (peerInfo.TotalDifficulty > _blockTree.BestSuggested.TotalDifficulty)
                            {
                                _syncRequested.Set();
                            }
                            else if (peerInfo.TotalDifficulty == _blockTree.BestSuggested.TotalDifficulty
                                     && peerInfo.HeadHash != _blockTree.BestSuggested.Hash)
                            {
                                Block block = _blockTree.FindBlock(_blockTree.BestSuggested.Hash, false);
                                peerInfo.Peer.SendNewBlock(block);
                                if (_logger.IsDebug) _logger.Debug($"Sending my best block {block} to {peerInfo}");
                            }
                        }

                        initCancelSource.Dispose();
                        linkedSource.Dispose();
                    });
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                if(_logger.IsError) _logger.Error($"Init peer loop encountered an exception {e}");
            }
            
            if(_logger.IsError) _logger.Error($"Exiting the peer loop");
        }

        private async Task RunSyncLoop()
        {
            while (true)
            {
                _syncRequested.Wait(_syncLoopCancelTokenSource.Token);
                _syncRequested.Reset();
                /* If block tree is processing blocks from DB then we are not going to start the sync process.
                 * In the future it may make sense to run sync anyway and let DB loader know that there are more blocks waiting.
                 * */

                if (!_blockTree.CanAcceptNewBlocks) continue;

                if (!AnyPeersWorthSyncingWithAreKnown()) continue;

                while (true)
                {
                    if (_syncLoopCancelTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    var peerInfo = _currentSyncingPeerInfo = SelectBestPeerForSync();
                    if (peerInfo == null)
                    {
                        if (_logger.IsDebug)
                            _logger.Debug(
                                "No more peers with better block available, finishing sync process, " +
                                $"best known block #: {_blockTree.BestKnownNumber}, " +
                                $"best peer block #: {(_peers.Values.Any() ? _peers.Values.Max(x => x.HeadNumber) : 0)}");
                        break;
                    }

                    SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Started)
                    {
                        NodeBestBlockNumber = peerInfo.HeadNumber,
                        OurBestBlockNumber = _blockTree.BestKnownNumber
                    });

                    if (_logger.IsDebug) _logger.Debug($"Starting sync process with {peerInfo} - best known block #: {_blockTree.BestKnownNumber}");

                    _peerSyncCancellationTokenSource = new CancellationTokenSource();
                    var peerSynchronizationTask = SynchronizeWithPeerAsync(peerInfo);
                    await peerSynchronizationTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            if (_logger.IsDebug) // only reports this error when viewed in the Debug mode
                            {
                                if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x is TimeoutException))
                                {
                                    _logger.Debug($"Stopping sync with node: {peerInfo}. {t.Exception?.Message}");
                                }
                                else
                                {
                                    _logger.Error($"Stopping sync with node: {peerInfo}. Error in the sync process.", t.Exception);
                                }
                            }

                            RemovePeer(peerInfo.Peer);
                            if (_logger.IsTrace) _logger.Trace($"Sync with {peerInfo} failed. Removed node from sync peers.");
                            SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Failed)
                            {
                                NodeBestBlockNumber = peerInfo.HeadNumber,
                                OurBestBlockNumber = _blockTree.BestKnownNumber
                            });
                        }
                        else if (t.IsCanceled || _peerSyncCancellationTokenSource.IsCancellationRequested)
                        {
                            if (_requestedSyncCancelDueToBetterPeer)
                            {
                                if (_logger.IsDebug) _logger.Debug($"Cancelled sync with {_currentSyncingPeerInfo} due to connection with better peer.");
                                _requestedSyncCancelDueToBetterPeer = false;
                            }
                            else
                            {
                                RemovePeer(peerInfo.Peer);
                                if (_logger.IsTrace) _logger.Trace($"Sync with {peerInfo} canceled. Removed node from sync peers.");
                                SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Cancelled)
                                {
                                    NodeBestBlockNumber = peerInfo.HeadNumber,
                                    OurBestBlockNumber = _blockTree.BestKnownNumber
                                });
                            }
                        }
                        else if (t.IsCompleted)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Sync process finished with {peerInfo}. Best known block is ({_blockTree.BestKnownNumber})");
                            SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Completed)
                            {
                                NodeBestBlockNumber = peerInfo.HeadNumber,
                                OurBestBlockNumber = _blockTree.BestKnownNumber
                            });
                        }

                        if (_logger.IsDebug)
                            _logger.Debug(
                                $"Finished peer sync process [{(t.IsFaulted ? "FAULTED" : t.IsCanceled ? "CANCELED" : t.IsCompleted ? "COMPLETED" : "OTHER")}] with {peerInfo}], " +
                                $"best known block #: {_blockTree.BestKnownNumber} ({_blockTree.BestKnownNumber}), " +
                                $"best peer block #: {peerInfo.HeadNumber} ({peerInfo.HeadNumber})");

                        var source = _peerSyncCancellationTokenSource;
                        _peerSyncCancellationTokenSource = null;
                        source?.Dispose();
                    }, _syncLoopCancelTokenSource.Token);
                }
            }
        }

        private bool AnyPeersWorthSyncingWithAreKnown()
        {
            foreach (KeyValuePair<PublicKey, PeerInfo> peer in _peers)
            {
                if (peer.Value.TotalDifficulty > _blockTree.BestSuggested.TotalDifficulty)
                {
                    return true;
                }
            }

            return false;
        }

        public int ChainId => _blockTree.ChainId;
        public BlockHeader Genesis => _blockTree.Genesis;
        public BlockHeader Head => _blockTree.Head;
        public UInt256 HeadNumber => _blockTree.Head.Number;
        public UInt256 TotalDifficulty => _blockTree.Head.TotalDifficulty ?? 0;
        public event EventHandler<SyncEventArgs> SyncEvent;

        public byte[][] GetNodeData(Keccak[] keys)
        {
            byte[][] values = new byte[keys.Length][];
            for (int i = 0; i < keys.Length; i++)
            {
                values[i] = _stateDb.Get(keys[i]);
            }

            return values;
        }

        public TransactionReceipt[][] GetReceipts(Keccak[] blockHashes)
        {
            TransactionReceipt[][] transactionReceipts = new TransactionReceipt[blockHashes.Length][];
            for (int blockIndex = 0; blockIndex < blockHashes.Length; blockIndex++)
            {
                Block block = Find(blockHashes[blockIndex]);
                TransactionReceipt[] blockTransactionReceipts = new TransactionReceipt[block?.Transactions.Length ?? 0];
                for (int receiptIndex = 0; receiptIndex < (block?.Transactions.Length ?? 0); receiptIndex++)
                {
                    if (block == null)
                    {
                        continue;
                    }

                    blockTransactionReceipts[receiptIndex] = _receiptStorage.Get(block.Transactions[receiptIndex].Hash);
                }

                transactionReceipts[blockIndex] = blockTransactionReceipts;
            }

            return transactionReceipts;
        }

        public Block Find(Keccak hash)
        {
            return _blockTree.FindBlock(hash, false);
        }

        public Block Find(UInt256 number)
        {
            return _blockTree.Head.Number >= number ? _blockTree.FindBlock(number) : null;
        }

        public Block[] Find(Keccak hash, int numberOfBlocks, int skip, bool reverse)
        {
            return _blockTree.FindBlocks(hash, numberOfBlocks, skip, reverse);
        }

        private LruCache<Keccak, object> _recentlySuggested = new LruCache<Keccak, object>(8);
        private object _dummyValue = new object();

        public void AddNewBlock(Block block, PublicKey nodeWhoSentTheBlock)
        {
            _peers.TryGetValue(nodeWhoSentTheBlock, out PeerInfo peer);
            if (peer == null)
            {
                string errorMessage = $"Received a new block from an unknown peer {nodeWhoSentTheBlock.ToShortString()}";
                if (_logger.IsDebug) _logger.Debug(errorMessage);
                return;
            }

            if ((block.TotalDifficulty ?? 0) > peer.TotalDifficulty)
            {
                peer.HeadNumber = block.Number;
                peer.TotalDifficulty = block.TotalDifficulty ?? peer.TotalDifficulty;
            }

            if ((block.TotalDifficulty ?? 0) < _blockTree.BestSuggested.TotalDifficulty)
            {
                return;
            }

            if (block.Number > _blockTree.BestKnownNumber + 8)
            {
                // ignore blocks when syncing in a simple non-locking way
                return;
            }

            lock (_recentlySuggested)
            {
                if (_recentlySuggested.Get(block.Hash) != null)
                {
                    return;
                }

                _recentlySuggested.Set(block.Hash, _dummyValue);
            }

            if (_logger.IsTrace) _logger.Trace($"Adding new block {block.Hash} ({block.Number}) from {nodeWhoSentTheBlock.ToShortString()}");

            if (block.Number <= _blockTree.BestKnownNumber + 1)
            {
                if (_logger.IsInfo) _logger.Info($"Suggesting a new block {block.ToString(Block.Format.HashNumberAndTx)} from {nodeWhoSentTheBlock.ToShortString()}");
                if (_logger.IsTrace) _logger.Trace($"{block}");

                AddBlockResult result = _blockTree.SuggestBlock(block);
                if (_logger.IsTrace) _logger.Trace($"{block.Hash} ({block.Number}) adding result is {result}");
                if (result == AddBlockResult.UnknownParent)
                {
                    /* here we want to cover scenario when our peer is reorganizing and sends us a head block
                     * from a new branch and we need to sync previous blocks as we do not know this block's parent */
                    _syncRequested.Set();
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Received a block {block.Hash} ({block.Number}) from {nodeWhoSentTheBlock} - need to resync");
                _syncRequested.Set();
            }
        }

        public void HintBlock(Keccak hash, UInt256 number, PublicKey receivedFrom)
        {
            if (!_peers.TryGetValue(receivedFrom, out PeerInfo peerInfo))
            {
                if (_logger.IsDebug) _logger.Debug($"Received a block hint from an unknown peer {receivedFrom}, ignoring");
                return;
            }

            if (number > _blockTree.BestKnownNumber + 8)
            {
                // ignore blocks when syncing in a simple non-locking way
                return;
            }

            if (number > peerInfo.HeadNumber)
            {
                peerInfo.HeadNumber = number;

                lock (_recentlySuggested)
                {
                    if (_recentlySuggested.Get(hash) != null)
                    {
                        return;
                    }

                    /* do not add as this is a hint only */
                }

                _peerRefreshQueue.Add(peerInfo);
            }
        }

        public void AddPeer(ISynchronizationPeer syncPeer)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Adding synchronization peer {syncPeer.Node:s}");
            if (!_isInitialized)
            {
                if (_logger.IsTrace) _logger.Trace($"Synchronization is disabled, adding peer is blocked: {syncPeer.Node:s}");
                return;
            }

            if (_peers.ContainsKey(syncPeer.Node.Id))
            {
                if (_logger.IsDebug) _logger.Debug($"Sync peer already in peers collection: {syncPeer.Node:s}");
                return;
            }

            var peerInfo = new PeerInfo(syncPeer);
            _peers.TryAdd(syncPeer.Node.Id, peerInfo);
            Metrics.SyncPeers = _peers.Count;

            _peerRefreshQueue.Add(peerInfo);
        }

        public void RemovePeer(ISynchronizationPeer syncPeer)
        {
            if (_logger.IsTrace) _logger.Trace($"Removing synchronization peer {syncPeer.Node:s}");
            if (!_isInitialized)
            {
                if (_logger.IsTrace) _logger.Trace($"Synchronization is disabled, removing peer is blocked: {syncPeer.Node:s}");
                return;
            }

            if (!_peers.TryRemove(syncPeer.Node.Id, out var peerInfo))
            {
                //possible if sync failed - we remove peer and eventually initiate disconnect, which calls remove peer again
                return;
            }

            Metrics.SyncPeers = _peers.Count;

            if (_currentSyncingPeerInfo?.Peer.Node.Id.Equals(syncPeer.Node.Id) ?? false)
            {
                if (_logger.IsTrace) _logger.Trace($"Requesting peer cancel with: {syncPeer.Node:s}");
                _peerSyncCancellationTokenSource?.Cancel();
            }

            if (_initCancelTokens.TryGetValue(syncPeer.Node.Id, out CancellationTokenSource initCancelTokenSource))
            {
                initCancelTokenSource?.Cancel();
            }
        }

        public int GetPeerCount()
        {
            return _peers.Count;
        }

        private Task _syncLoopTask;

        private Task _initPeerLoopTask;

        public void Start()
        {
            _isInitialized = true;
            _blockTree.NewHeadBlock += OnNewHeadBlock;

            _syncLoopTask = Task.Factory.StartNew(
                RunSyncLoop,
                _syncLoopCancelTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Sync loop encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsDebug) _logger.Debug("Sync loop stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsDebug) _logger.Debug("Sync loop complete.");
                }
            });

            _initPeerLoopTask = Task.Factory.StartNew(
                RunRefreshPeerLoop,
                _syncLoopCancelTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Init peer loop encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsDebug) _logger.Debug("Init peer loop stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsDebug) _logger.Debug("Init peer loop complete.");
                }
            });

            StartSyncTimer();
        }

        public async Task StopAsync()
        {
            var key = _perfService.StartPerfCalc();
            _isInitialized = false;
            StopSyncTimer();

            _peerSyncCancellationTokenSource?.Cancel();
            _syncLoopCancelTokenSource?.Cancel();

            await (_syncLoopTask ?? Task.CompletedTask);
            await (_initPeerLoopTask ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Sync shutdown complete.. please wait for all components to close");
            _perfService.EndPerfCalc(key, "Close: SynchronizationManager");
        }

        private DateTime _lastFullInfo = DateTime.Now;

        private void StartSyncTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting sync timer");
            _syncTimer = new System.Timers.Timer(_blockchainConfig.SyncTimerInterval);
            _syncTimer.Elapsed += (s, e) =>
            {
                try
                {
                    _syncTimer.Enabled = false;
                    var initPeerCount = _peers.Count(p => p.Value.IsInitialized);

                    if (DateTime.Now - _lastFullInfo > TimeSpan.FromSeconds(120) && _logger.IsDebug)
                    {
                        if(_logger.IsDebug) _logger.Debug("Sync peers list:");
                        foreach ((PublicKey nodeId,  PeerInfo peerInfo) in _peers)
                        {
                            if(_logger.IsDebug) _logger.Debug($"{peerInfo}");
                        }

                        _lastFullInfo = DateTime.Now;
                    }
                    else if (initPeerCount != _lastSyncPeersCount)
                    {
                        _lastSyncPeersCount = initPeerCount;
                        if (_logger.IsInfo) _logger.Info($"Sync peers {initPeerCount}({_peers.Count})/{_blockchainConfig.SyncPeersMaxCount} {(_currentSyncingPeerInfo != null ? $"(sync in progress with {_currentSyncingPeerInfo})" : string.Empty)}");
                    }
                    else if (initPeerCount == 0)
                    {
                        if (_logger.IsInfo) _logger.Info($"Sync peers 0, searching for peers to sync with...");
                    }

                    CheckIfSyncingWithFastestPeer();
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("Sync timer failed", exception);
                }
                finally
                {
                    _syncTimer.Enabled = true;
                }
            };

            _syncTimer.Start();
        }

        private void CheckIfSyncingWithFastestPeer()
        {
            var bestLatencyPeerInfo = _peers.Values.Where(x => x.HeadNumber > _blockTree.BestKnownNumber).OrderBy(x => _stats.GetOrAdd(x.Peer.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000).FirstOrDefault();
            if (bestLatencyPeerInfo != null && _currentSyncingPeerInfo != null && _currentSyncingPeerInfo.Peer?.Node.Id != bestLatencyPeerInfo.Peer?.Node.Id)
            {
                if (_logger.IsTrace) _logger.Trace("Checking if any available peer is faster than current sync peer");
                CancelCurrentPeerSyncIfWorse(bestLatencyPeerInfo, ComparedPeerType.Existing);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"NotSyncing or Syncing with fastest peer: bestLatencyPeer: {bestLatencyPeerInfo?.ToString() ?? "none"}, currentSyncingPeer: {_currentSyncingPeerInfo?.ToString() ?? "none"}");
            }
        }

        private void StopSyncTimer()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping sync timer");
                _syncTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during sync timer stop", e);
            }
        }

        public enum ComparedPeerType
        {
            New,
            Existing
        }

        private void CancelCurrentPeerSyncIfWorse(PeerInfo peerInfo, ComparedPeerType comparedPeerType)
        {
            if (_currentSyncingPeerInfo == null)
            {
                return;
            }

            //As we deal with UInt256 if we subtract bigger value from smaller value we get very big value as a result (overflow) which is incorrect (unsigned)
            BigInteger chainLengthDiff = peerInfo.HeadNumber > _blockTree.BestKnownNumber ? peerInfo.HeadNumber - _blockTree.BestKnownNumber : 0;
            chainLengthDiff = BigInteger.Max(chainLengthDiff, (peerInfo.TotalDifficulty - (BigInteger) (_blockTree.BestSuggested?.TotalDifficulty ?? UInt256.Zero)) / (_blockTree.BestSuggested?.Difficulty ?? 1));
            if (chainLengthDiff < _blockchainConfig.MinAvailableBlockDiffForSyncSwitch)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping latency switch due to lower chain length diff than threshold - chain length diff: {chainLengthDiff}, threshold: {_blockchainConfig.MinAvailableBlockDiffForSyncSwitch}");
                return;
            }


            var currentSyncPeerLatency = _stats.GetOrAdd(_currentSyncingPeerInfo?.Peer?.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000;
            var newPeerLatency = _stats.GetOrAdd(peerInfo.Peer.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100001;
            if (currentSyncPeerLatency - newPeerLatency >= _blockchainConfig.MinLatencyDiffForSyncSwitch)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"{comparedPeerType} peer with better latency, requesting cancel for current sync process{Environment.NewLine}" +
                                  $"{comparedPeerType} {peerInfo}, Latency: {newPeerLatency}{Environment.NewLine}" +
                                  $"Current peer: {_currentSyncingPeerInfo}, Latency: {currentSyncPeerLatency}, Best Known: {_blockTree.BestKnownNumber}, Available @ Peer: {peerInfo.HeadNumber}");
                }

                _requestedSyncCancelDueToBetterPeer = true;
                _peerSyncCancellationTokenSource?.Cancel();
            }
            else
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"{comparedPeerType} peer with worse latency{Environment.NewLine}" +
                                  $"{comparedPeerType} {peerInfo}, Latency: {newPeerLatency}{Environment.NewLine}" +
                                  $"Current {_currentSyncingPeerInfo}, Latency: {currentSyncPeerLatency}");
                }
            }
        }

        private void OnNewHeadBlock(object sender, BlockEventArgs blockEventArgs)
        {
            Block block = blockEventArgs.Block;
            int counter = 0;
            foreach ((_, PeerInfo peerInfo) in _peers)
            {
                if (peerInfo.TotalDifficulty < (block.TotalDifficulty ?? UInt256.Zero))
                {
                    peerInfo.Peer.SendNewBlock(block);
                    counter++;
                }
            }

            if (counter > 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Broadcasting block {block.ToString(Block.Format.Short)} to {counter} peers.");
            }
        }

        private PeerInfo SelectBestPeerForSync()
        {
            var availablePeers = _peers.Values.Where(x => x.TotalDifficulty > _blockTree.BestSuggested.TotalDifficulty).Where(x => x.IsInitialized).Select(x => new {PeerInfo = x, AvLat = _stats.GetOrAdd(x.Peer?.Node).GetAverageLatency(NodeLatencyStatType.BlockHeaders)})
                .OrderBy(x => x.AvLat ?? 100000).ToArray();
            if (!availablePeers.Any())
            {
                return null;
            }

            if (_logger.IsDebug) _logger.Debug($"Candidates for Sync: {Environment.NewLine}{string.Join(Environment.NewLine, availablePeers.Select(x => $"{x.PeerInfo} | BlockHeaderAvLatency: {x.AvLat?.ToString() ?? "none"}").ToArray())}");
            var selectedInfo = availablePeers.First().PeerInfo;
            if (selectedInfo.Peer.Node.Id == _currentSyncingPeerInfo?.Peer?.Node.Id)
            {
                if (_logger.IsDebug) _logger.Debug($"Potential error, selecting same peer for sync as prev sync peer, id: {selectedInfo}");
            }

            return selectedInfo;
        }

        [Todo(Improve.Readability, "Let us review the cancellation approach here")]
        private async Task SynchronizeWithPeerAsync(PeerInfo peerInfo)
        {
            bool wasCanceled = false;

            ISynchronizationPeer peer = peerInfo.Peer;

            const int maxLookup = MaxReorganizationLength;
            int ancestorLookupLevel = 0;
            int emptyBlockListCounter = 0;

            UInt256 currentNumber = UInt256.Min(_blockTree.BestKnownNumber, peerInfo.HeadNumber - 1);
            while (peerInfo.TotalDifficulty > (_blockTree.BestSuggested?.TotalDifficulty ?? 0) && currentNumber <= peerInfo.HeadNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"Continue syncing with {peerInfo} (our best {_blockTree.BestKnownNumber})");

                if (ancestorLookupLevel > maxLookup)
                {
                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {peerInfo}");
                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
                }

                if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Sync with {peerInfo} cancelled");
                    return;
                }

                UInt256 blocksLeft = peerInfo.HeadNumber - currentNumber;
                int blocksToRequest = (int) BigInteger.Min(blocksLeft + 1, _currentBatchSize);
                if (_logger.IsTrace) _logger.Trace($"Sync request {currentNumber}+{blocksToRequest} to peer {peerInfo.Peer.Node.Id} with {peerInfo.HeadNumber} blocks. Got {currentNumber} and asking for {blocksToRequest} more.");

                Task<BlockHeader[]> headersTask = peer.GetBlockHeaders(currentNumber, blocksToRequest, 0, _peerSyncCancellationTokenSource.Token);
                BlockHeader[] headers = await headersTask;
                if (headersTask.IsCanceled)
                {
                    if (_logger.IsTrace) _logger.Trace("Headers task cancelled");
                    wasCanceled = true;
                    break;
                }

                if (headersTask.IsFaulted)
                {
                    _sinceLastTimeout = 0;
                    if (headersTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        DecreaseBatchSize();
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve headers when synchronizing (Timeout)", headersTask.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve headers when synchronizing", headersTask.Exception);
                    }

                    throw headersTask.Exception;
                }

                if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                {
                    if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                    return;
                }

                List<Keccak> hashes = new List<Keccak>();
                Dictionary<Keccak, BlockHeader> headersByHash = new Dictionary<Keccak, BlockHeader>();
                for (int i = 1; i < headers.Length; i++)
                {
                    if (headers[i] == null)
                    {
                        break;
                    }

                    hashes.Add(headers[i].Hash);
                    headersByHash[headers[i].Hash] = headers[i];
                }

                Task<Block[]> bodiesTask = peer.GetBlocks(hashes.ToArray(), _peerSyncCancellationTokenSource.Token);
                Block[] blocks = await bodiesTask;
                if (bodiesTask.IsCanceled)
                {
                    wasCanceled = true;
                    if (_logger.IsTrace) _logger.Trace("Bodies task cancelled");
                    break;
                }

                if (bodiesTask.IsFaulted)
                {
                    _sinceLastTimeout = 0;
                    if (bodiesTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve bodies when synchronizing (Timeout)", bodiesTask.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve bodies when synchronizing", bodiesTask.Exception);
                    }

                    throw bodiesTask.Exception;
                }

                if (blocks.Length == 0 && blocksLeft == 1)
                {
                    if (_logger.IsDebug) _logger.Debug($"{peerInfo} does not have block body for {hashes[0]}");
                }
                
                if (blocks.Length == 0 && ++emptyBlockListCounter >= 10)
                {
                    if (_currentBatchSize == MinBatchSize)
                    {
                        if (_logger.IsInfo) _logger.Info($"Received no blocks from {_currentSyncingPeerInfo} in response to {blocksToRequest} blocks requested. Cancelling.");
                        throw new EthSynchronizationException("Peer sent an empty block list");
                    }

                    if (_logger.IsInfo) _logger.Info($"Received no blocks from {_currentSyncingPeerInfo} in response to {blocksToRequest} blocks requested. Decreasing batch size from {_currentBatchSize}.");
                    DecreaseBatchSize();
                    continue;
                }

                if (blocks.Length != 0)
                {
                    if (_logger.IsTrace) _logger.Trace($"Blocks length is {blocks.Length}, counter is {emptyBlockListCounter}");
                    emptyBlockListCounter = 0;
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Blocks length is 0, counter is {emptyBlockListCounter}");
                    continue;
                }

                _sinceLastTimeout++;
                if (_sinceLastTimeout > 8)
                {
                    IncreaseBatchSize();
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }

                    blocks[i].Header = headersByHash[hashes[i]];
                }

                if (blocks.Length > 0)
                {
                    Block parent = _blockTree.FindParent(blocks[0]);
                    if (parent == null)
                    {
                        ancestorLookupLevel += _currentBatchSize;
                        currentNumber = currentNumber >= _currentBatchSize ? (currentNumber - (UInt256) _currentBatchSize) : UInt256.Zero;
                        continue;
                    }
                }

                /* // fast sync receipts download when ETH63 implemented fully
                if (await DownloadReceipts(blocks, peer)) break; */

                // Parity 1.11 non canonical blocks when testing on 27/06
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (i != 0 && blocks[i].ParentHash != blocks[i - 1].Hash)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {peerInfo}");
                        throw new EthSynchronizationException("Peer sent an inconsistent block list");
                    }
                }

                var exceptions = new ConcurrentQueue<Exception>();
                Parallel.For(0, blocks.Length, (i, state) =>
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        if (!_sealValidator.ValidateSeal(blocks[i].Header))
                        {
                            state.Stop();
                            throw new EthSynchronizationException("Peer sent a block with an invalid seal");
                        }
                    }
                    catch (Exception e)
                    {
                        exceptions.Enqueue(e);
                    }
                });

                if (exceptions.Count > 0)
                {
                    throw new AggregateException(exceptions);
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        return;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received {blocks[i]} from {peer.Node:s}");

                    if (!_blockValidator.ValidateSuggestedBlock(blocks[i]))
                    {
                        if (_logger.IsWarn) _logger.Warn($"Block {blocks[i].Number} skipped (validation failed)");
                        continue;
                    }

                    AddBlockResult addResult = _blockTree.SuggestBlock(blocks[i]);
                    switch (addResult)
                    {
                        case AddBlockResult.UnknownParent:
                        {
                            if (_logger.IsTrace)
                                _logger.Trace($"Block {blocks[i].Number} ignored (unknown parent)");
                            if (i == 0)
                            {
                                const string message = "Peer sent orphaned blocks inside the batch";
                                _logger.Error(message);
                                throw new EthSynchronizationException(message);
                            }
                            else
                            {
                                const string message = "Peer sent an inconsistent batch of block headers";
                                _logger.Error(message);
                                throw new EthSynchronizationException(message);
                            }
                        }
                        case AddBlockResult.CannotAccept:
                            return;
                        case AddBlockResult.InvalidBlock:
                            throw new EthSynchronizationException("Peer sent an invalid block");
                    }

                    if (_logger.IsTrace) _logger.Trace($"Block {blocks[i].Number} suggested for processing");
                }

                currentNumber = blocks[blocks.Length - 1].Number;
                if (_blockTree.BestKnownNumber > _lastSyncNumber + 10000 || _blockTree.BestKnownNumber < _lastSyncNumber)
                {
                    _lastSyncNumber = _blockTree.BestKnownNumber;
                    if (_logger.IsDebug) _logger.Debug($"Downloading blocks. Current best at {_blockTree.BestSuggested?.ToString(BlockHeader.Format.Short)}");
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Stopping sync processes with {peerInfo}, wasCancelled: {wasCanceled}");
        }

        private void IncreaseBatchSize() => _currentBatchSize = Math.Min(MaxBatchSize, _currentBatchSize * 2);

        private void DecreaseBatchSize() => _currentBatchSize = Math.Max(MinBatchSize, _currentBatchSize / 2);

        public static int InitTimeout = 10000;

        private async Task RefreshPeerInfo(ISynchronizationPeer syncPeer, CancellationToken token)
        {
            if (_logger.IsTrace) _logger.Trace($"Requesting head block info from {syncPeer.Node:s}");
            var result = _peers.TryGetValue(syncPeer.Node.Id, out PeerInfo peerInfo);
            if (!result)
            {
                if (_logger.IsDebug) _logger.Debug($"Initializing PeerInfo failed for {syncPeer.Node:s}");
                throw new EthSynchronizationException($"Initializing peer info failed for {syncPeer.Node:s}");
            }

            Task<BlockHeader> getHeadHeaderTask = syncPeer.GetHeadBlockHeader(token);
            Task delayTask = Task.Delay(InitTimeout, token);
            Task firstToComplete = await Task.WhenAny(getHeadHeaderTask, delayTask);
            await firstToComplete.ContinueWith(
                t =>
                {
                    if (firstToComplete.IsFaulted || firstToComplete == delayTask)
                    {
                        if (_logger.IsTrace) _logger.Trace($"InitPeerInfo failed for node: {syncPeer.Node:s}{Environment.NewLine}{t.Exception}");
                        RemovePeer(syncPeer);
                        SyncEvent?.Invoke(this, new SyncEventArgs(syncPeer, peerInfo.IsInitialized ? SyncStatus.Failed : SyncStatus.InitFailed));
                    }
                    else if (firstToComplete.IsCanceled)
                    {
                        RemovePeer(syncPeer);
                        SyncEvent?.Invoke(this, new SyncEventArgs(syncPeer, peerInfo.IsInitialized ? SyncStatus.Cancelled : SyncStatus.InitCancelled));
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Received head block info from {syncPeer.Node:s} with head block numer {getHeadHeaderTask.Result}");
                        if (!peerInfo.IsInitialized)
                        {
                            SyncEvent?.Invoke(
                                this,
                                new SyncEventArgs(syncPeer, SyncStatus.InitCompleted)
                                {
                                    NodeBestBlockNumber = getHeadHeaderTask.Result.Number,
                                    OurBestBlockNumber = _blockTree.BestKnownNumber
                                });
                        }

                        peerInfo.HeadNumber = getHeadHeaderTask.Result.Number;
                        peerInfo.HeadHash = getHeadHeaderTask.Result.Hash;

                        BlockHeader bestSuggested = _blockTree.BestSuggested;
                        if (getHeadHeaderTask.Result.ParentHash == bestSuggested.Hash)
                        {
                            peerInfo.TotalDifficulty = (bestSuggested.TotalDifficulty ?? UInt256.Zero) + getHeadHeaderTask.Result.Difficulty;
                        }

                        peerInfo.IsInitialized = true;
                    }
                }, token);
        }

        private class PeerInfo
        {
            public PeerInfo(ISynchronizationPeer peer)
            {
                Peer = peer;
                TotalDifficulty = peer.TotalDifficultyOnSessionStart;
            }

            public bool IsInitialized { get; set; }
            public ISynchronizationPeer Peer { get; }
            public UInt256 TotalDifficulty { get; set; }
            public UInt256 HeadNumber { get; set; }
            public Keccak HeadHash { get; set; }

            public override string ToString()
            {
                return ToString(true);
            }

            private string ToString(bool fullFormat)
            {
                if (fullFormat)
                {
                    return $"[Peer|{Peer?.Node:s}|{HeadNumber}|{Peer?.ClientId}]";
                }

                return $"[Peer|{Peer?.Node:s}|{HeadNumber}]";
            }
        }

        [Todo(Improve.MissingFunctionality, "Eth63 / fast sync can download receipts using this method. Fast sync is not implemented although its methods and serializers are already written.")]
        private async Task<bool> DownloadReceipts(Block[] blocks, ISynchronizationPeer peer)
        {
            Block[] blocksWithTransactions = blocks.Where(b => b.Transactions.Length != 0).ToArray();
            if (blocksWithTransactions.Length != 0)
            {
                Task<TransactionReceipt[][]> receiptsTask = peer.GetReceipts(blocksWithTransactions.Select(b => b.Hash).ToArray(), _peerSyncCancellationTokenSource.Token);
                TransactionReceipt[][] transactionReceipts = await receiptsTask;
                if (receiptsTask.IsCanceled)
                {
                    return true;
                }

                if (receiptsTask.IsFaulted)
                {
                    _sinceLastTimeout = 0;
                    if (receiptsTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve receipts when synchronizing (Timeout)", receiptsTask.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve receipts when synchronizing", receiptsTask.Exception);
                    }

                    throw receiptsTask.Exception;
                }

                for (int blockIndex = 0; blockIndex < blocksWithTransactions.Length; blockIndex++)
                {
                    long gasUsedTotal = 0;
                    for (int txIndex = 0; txIndex < blocksWithTransactions[blockIndex].Transactions.Length; txIndex++)
                    {
                        TransactionReceipt transactionReceipt = transactionReceipts[blockIndex][txIndex];
                        if (transactionReceipt == null)
                        {
                            throw new DataException($"Missing receipt for {blocksWithTransactions[blockIndex].Hash}->{txIndex}");
                        }

                        transactionReceipt.Index = txIndex;
                        transactionReceipt.BlockHash = blocksWithTransactions[blockIndex].Hash;
                        transactionReceipt.BlockNumber = blocksWithTransactions[blockIndex].Number;
                        transactionReceipt.TransactionHash = blocksWithTransactions[blockIndex].Transactions[txIndex].Hash;
                        gasUsedTotal += transactionReceipt.GasUsed;
                        transactionReceipt.GasUsedTotal = gasUsedTotal;
                        transactionReceipt.Recipient = blocksWithTransactions[blockIndex].Transactions[txIndex].To;

                        // only after execution
                        // receipt.Sender = blocksWithTransactions[blockIndex].Transactions[txIndex].SenderAddress; 
                        // receipt.Error = ...
                        // receipt.ContractAddress = ...

                        _receiptStorage.Add(transactionReceipt);
                    }
                }
            }

            return false;
        }
    }
}