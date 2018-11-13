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
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Stats.Model;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class SynchronizationManager : ISynchronizationManager
    {
        public const int MinBatchSize = 8;
        private int _batchSize = 256;
        public const int MaxBatchSize = 256;
        private int _sinceLastTimeout = 0;

        private readonly ILogger _logger;
        private readonly IBlockValidator _blockValidator;
        private readonly IHeaderValidator _headerValidator;
        private readonly IPerfService _perfService;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ConcurrentDictionary<NodeId, PeerInfo> _peers = new ConcurrentDictionary<NodeId, PeerInfo>();
        private readonly ConcurrentDictionary<NodeId, CancellationTokenSource> _initCancelTokens = new ConcurrentDictionary<NodeId, CancellationTokenSource>();

        private readonly ITransactionValidator _transactionValidator;
        private readonly IDb _stateDb;
        private readonly IBlockchainConfig _blockchainConfig;
        private readonly IBlockTree _blockTree;

        private readonly object _isSyncingLock = new object();
        private bool _isSyncing;
        private bool _isInitialized;

        private PeerInfo _currentSyncingPeerInfo;
        private System.Timers.Timer _syncTimer;

        private CancellationTokenSource _peerSyncCancellationTokenSource;
        private bool _requestedSyncCancelDueToBetterPeer;
        private CancellationTokenSource _aggregateSyncCancellationTokenSource = new CancellationTokenSource();
        private int _lastSyncPeersCount;

        public SynchronizationManager(
            IDb stateDb,
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            IHeaderValidator headerValidator,
            ITransactionValidator transactionValidator,
            ILogManager logManager,
            IBlockchainConfig blockchainConfig,
            IPerfService perfService,
            IReceiptStorage receiptStorage)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _blockchainConfig = blockchainConfig ?? throw new ArgumentNullException(nameof(blockchainConfig));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _receiptStorage = receiptStorage;

            _transactionValidator = transactionValidator ?? throw new ArgumentNullException(nameof(transactionValidator));

            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));

            if (_logger.IsDebug) _logger.Debug($"Initialized SynchronizationManager with head block {Head.ToString(BlockHeader.Format.Short)}");
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
            TransactionReceipt[][] receipts = new TransactionReceipt[blockHashes.Length][];
            for (int blockIndex = 0; blockIndex < blockHashes.Length; blockIndex++)
            {
                Block block = Find(blockHashes[blockIndex]);
                TransactionReceipt[] blockReceipts = new TransactionReceipt[block?.Transactions.Length ?? 0];
                for (int receiptIndex = 0; receiptIndex < (block?.Transactions.Length ?? 0); receiptIndex++)
                {
                    if (block == null)
                    {
                        continue;
                    }

                    blockReceipts[receiptIndex] = _receiptStorage.Get(block.Transactions[receiptIndex].Hash);
                }

                receipts[blockIndex] = blockReceipts;
            }

            return receipts;
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

        public void AddNewBlock(Block block, NodeId receivedFrom)
        {
            // TODO: validation

            _peers.TryGetValue(receivedFrom, out PeerInfo peerInfo);
            if (peerInfo == null)
            {
                string errorMessage = $"Received a new block from an unknown peer {receivedFrom}";
                _logger.Error(errorMessage);
                return;
            }

            peerInfo.NumberAvailable = UInt256.Max(block.Number, peerInfo.NumberAvailable);
//            peerInfo.Difficulty = UInt256.Max(block.Difficulty, peerInfo.Difficulty);

            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    if (_logger.IsTrace) _logger.Trace($"Ignoring new block {block.Hash} while syncing");
                    return;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Adding new block {block.Hash} ({block.Number}) from {receivedFrom}");

            if (block.Number <= _blockTree.BestKnownNumber + 1)
            {
                if (_logger.IsTrace) _logger.Trace($"Suggesting a block {block.Hash} ({block.Number}) from {receivedFrom} with {block.Transactions.Length} transactions");
                if (_logger.IsTrace) _logger.Trace($"{block}");

                AddBlockResult result = _blockTree.SuggestBlock(block);
                if (_logger.IsTrace) _logger.Trace($"{block.Hash} ({block.Number}) adding result is {result}");
                if (result == AddBlockResult.UnknownParent)
                {
                    /* here we want to cover scenario when our peer is reorganizing and sends us a head block
                     * from a new branch and we need to sync previous blocks as we do not know this block's parent */
                    RequestSync();
                }
                else
                {
                    peerInfo.NumberReceived = block.Number;
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Received a block {block.Hash} ({block.Number}) from {receivedFrom} - need to resync");
                RequestSync();
            }
        }

        public void HintBlock(Keccak hash, UInt256 number, NodeId receivedFrom)
        {
            if (!_peers.TryGetValue(receivedFrom, out PeerInfo peerInfo))
            {
                if (_logger.IsTrace) _logger.Trace($"Received a block hint from an unknown peer {receivedFrom}, ignoring");
                return;
            }

            peerInfo.NumberAvailable = UInt256.Max(number, peerInfo.NumberAvailable);
        }

        public void AddNewTransaction(Transaction transaction, NodeId receivedFrom)
        {
            if (_logger.IsTrace) _logger.Trace($"Received a pending transaction {transaction.Hash} from {receivedFrom}");
        }

        public async Task AddPeer(ISynchronizationPeer synchronizationPeer)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Adding synchronization peer {synchronizationPeer.NodeId}");
            if (!_isInitialized)
            {
                if (_logger.IsTrace) _logger.Trace($"Synchronization is disabled, adding peer is blocked: {synchronizationPeer.NodeId}");
                return;
            }

            if (_peers.ContainsKey(synchronizationPeer.NodeId))
            {
                if (_logger.IsError) _logger.Error($"Sync peer already in peers collection: {synchronizationPeer.NodeId}");
                return;
            }

            var peerInfo = new PeerInfo(synchronizationPeer);
            _peers.TryAdd(synchronizationPeer.NodeId, peerInfo);

            var initCancelSource = _initCancelTokens[synchronizationPeer.NodeId] = new CancellationTokenSource();
            var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(initCancelSource.Token, _aggregateSyncCancellationTokenSource.Token);
            // ReSharper disable once MethodSupportsCancellation
            await InitPeerInfo(synchronizationPeer, linkedSource.Token).ContinueWith(t =>
            {
                _initCancelTokens.TryRemove(synchronizationPeer.NodeId, out _);
                if (t.IsFaulted)
                {
                    if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        if (_logger.IsTrace) _logger.Trace($"AddPeer failed due to timeout: {t.Exception.Message}");
                    }
                    else if (_logger.IsDebug) _logger.Debug("AddPeer failed {t.Exception}");
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsTrace) _logger.Trace($"Init peer info canceled: {synchronizationPeer.NodeId}");
                }
                else
                {
                    CheckIfNewPeerIsBetterSyncCandidate(peerInfo);
                    RequestSync();
                }

                initCancelSource.Dispose();
                linkedSource.Dispose();
            });
        }

        public void RemovePeer(ISynchronizationPeer synchronizationPeer)
        {
            if (_logger.IsTrace) _logger.Trace($"Removing synchronization peer {synchronizationPeer.NodeId}");
            if (!_isInitialized)
            {
                if (_logger.IsTrace) _logger.Trace($"Synchronization is disabled, removing peer is blocked: {synchronizationPeer.NodeId}");
                return;
            }

            if (!_peers.TryRemove(synchronizationPeer.NodeId, out var peerInfo))
            {
                //possible if sync failed - we remove peer and eventually initiate disconnect, which calls remove peer again
                return;
            }

            if (_currentSyncingPeerInfo?.Peer.NodeId.Equals(synchronizationPeer.NodeId) ?? false)
            {
                if (_logger.IsTrace) _logger.Trace($"Requesting peer cancel with: {synchronizationPeer.NodeId}");
                _peerSyncCancellationTokenSource?.Cancel();
            }

            if (_initCancelTokens.TryGetValue(synchronizationPeer.NodeId, out CancellationTokenSource initCancelTokenSource))
            {
                initCancelTokenSource.Cancel();
            }
        }

        public int GetPeerCount()
        {
            return _peers.Count;
        }

        public void Start()
        {
            _isInitialized = true;
            _blockTree.NewHeadBlock += OnNewHeadBlock;
            StartSyncTimer();
        }

        public async Task StopAsync()
        {
            var key = _perfService.StartPerfCalc();
            _isInitialized = false;
            StopSyncTimer();

            _aggregateSyncCancellationTokenSource?.Cancel();
            _peerSyncCancellationTokenSource?.Cancel();

            await Task.CompletedTask;

            // TODO: tks: with return before some perf calc that was started will never be finished
            if (_logger.IsInfo) _logger.Info("Sync shutdown complete.. please wait for all components to close");
            _perfService.EndPerfCalc(key, "Close: SynchronizationManager");
        }

        private void StartSyncTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting sync timer");
            _syncTimer = new System.Timers.Timer(_blockchainConfig.SyncTimerInterval);
            _syncTimer.Elapsed += (s, e) =>
            {
                _syncTimer.Enabled = false;
                if (_isInitialized)
                {
                    RequestSync();
                }

                var initPeerCount = _peers.Count(p => p.Value.IsInitialized);
                if (initPeerCount != _lastSyncPeersCount)
                {
                    _lastSyncPeersCount = initPeerCount;
                    if (_logger.IsInfo) _logger.Info($"Sync peers {initPeerCount}({_peers.Count})/{_blockchainConfig.SyncPeersMaxCount} {(_isSyncing ? $"(sync in progress with {_currentSyncingPeerInfo?.ToString()})" : string.Empty)}");
                }

                CheckIfSyncingWithFastestPeer();
                _syncTimer.Enabled = true;
            };

            _syncTimer.Start();
        }

        private void CheckIfSyncingWithFastestPeer()
        {
            var bestLatencyPeer = _peers.Values.Where(x => x.NumberAvailable > _blockTree.BestKnownNumber).OrderBy(x => x.Peer.NodeStats.GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000).FirstOrDefault();
            if (bestLatencyPeer != null && _currentSyncingPeerInfo != null && _currentSyncingPeerInfo.Peer?.NodeId != bestLatencyPeer.Peer?.NodeId)
            {
                if (_logger.IsTrace) _logger.Trace("Checking if any available peer is faster than current sync peer");
                CheckIfExistingPeerIsBetterSyncCandidate(bestLatencyPeer);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"NotSyncing or Syncing with fastest peer: bestLatencyPeer: {bestLatencyPeer?.ToString() ?? "none"}, currentSyncingPeer: {_currentSyncingPeerInfo?.ToString() ?? "none"}");
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

        private void CheckIfNewPeerIsBetterSyncCandidate(PeerInfo peerInfo)
        {
            CheckIfPeerIsBetterSyncCandidate(peerInfo, "New");
        }

        private void CheckIfExistingPeerIsBetterSyncCandidate(PeerInfo peerInfo)
        {
            CheckIfPeerIsBetterSyncCandidate(peerInfo, "Existing");
        }

        private void CheckIfPeerIsBetterSyncCandidate(PeerInfo peerInfo, string peerDesc)
        {
            lock (_isSyncingLock)
            {
                if (!_isSyncing || _currentSyncingPeerInfo == null)
                {
                    return;
                }
            }

            //As we deal with UInt256 if we substruct bigger value from smaller value we get very big value as a result (overflow) which is incorret (unsigned)
            var letencyDiff = peerInfo.NumberAvailable > _blockTree.BestKnownNumber ? peerInfo.NumberAvailable - _blockTree.BestKnownNumber : 0;
            if (letencyDiff < _blockchainConfig.MinAvailableBlockDiffForSyncSwitch)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping latency switch due to lower latency benefit than threshold - letencyDiff: {letencyDiff}, threshold: {_blockchainConfig.MinAvailableBlockDiffForSyncSwitch}");
                return;
            }

            var currentSyncPeerLatency = _currentSyncingPeerInfo?.Peer.NodeStats.GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100000;
            var newPeerLatency = peerInfo.Peer.NodeStats.GetAverageLatency(NodeLatencyStatType.BlockHeaders) ?? 100001;
            if (currentSyncPeerLatency - newPeerLatency >= _blockchainConfig.MinLatencyDiffForSyncSwitch)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"{peerDesc} peer with better latency, requesting cancel for current sync process{Environment.NewLine}" +
                                  $"{peerDesc} {peerInfo}, Latency: {newPeerLatency}{Environment.NewLine}" +
                                  $"Current peer: {_currentSyncingPeerInfo}, Latency: {currentSyncPeerLatency}");
                }

                _requestedSyncCancelDueToBetterPeer = true;
                _peerSyncCancellationTokenSource?.Cancel();
            }
            else
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"{peerDesc} peer with worse latency{Environment.NewLine}" +
                                  $"{peerDesc} {peerInfo}, Latency: {newPeerLatency}{Environment.NewLine}" +
                                  $"Current {_currentSyncingPeerInfo}, Latency: {currentSyncPeerLatency}");
                }
            }
        }

        private void OnNewHeadBlock(object sender, BlockEventArgs blockEventArgs)
        {
            // this is not critical (just beneficial) not to run in parallel with sync so the race condition here is totally acceptable
            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    return;
                }
            }

            Block block = blockEventArgs.Block;
            foreach ((NodeId nodeId, PeerInfo peerInfo) in _peers)
            {
                if (peerInfo.NumberAvailable < block.Number) // TODO: total difficulty instead
                {
                    peerInfo.Peer.SendNewBlock(block);
                }
            }
        }

        private void RequestSync()
        {
            /* If block tree is processing blocks from DB then we are not going to start the sync process.
             * In the future it may make sense to run sync anyway and let DB loader know that there are more blocks waiting.
             */
            if (!_blockTree.CanAcceptNewBlocks)
            {
                if (_logger.IsTrace) _logger.Trace("Block tree cannot accept new blocks - skipping sync call");
                return;
            }

            if (_aggregateSyncCancellationTokenSource.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug("Cancellation requested will not start sync");
                return;
            }

            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    if (_logger.IsTrace) _logger.Trace("Sync in progress - skipping sync call");
                    return;
                }

                _isSyncing = true;
            }

            var syncTask = Task.Run(() => SynchronizeAsync(_aggregateSyncCancellationTokenSource.Token), _aggregateSyncCancellationTokenSource.Token);
            syncTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error($"Error during sync process: {t.Exception}");
                }

                lock (_isSyncingLock)
                {
                    _isSyncing = false;
                }
            });
        }

        private async Task SynchronizeAsync(CancellationToken syncCancellationToken)
        {
            while (true)
            {
                if (syncCancellationToken.IsCancellationRequested)
                {
                    // it was throwing an exception here before, why? (tks 2018/08/24)
                    return;
                }

                var peerInfo = _currentSyncingPeerInfo = SelectBestPeerForSync();
                if (peerInfo == null)
                {
                    if (_logger.IsDebug)
                        _logger.Debug(
                            "No more peers with better block available, finishing sync process, " +
                            $"best known block #: {_blockTree.BestKnownNumber}, " +
                            $"best peer block #: {(_peers.Values.Any() ? _peers.Values.Max(x => x.NumberAvailable) : 0)}");
                    return;
                }

                SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Started)
                {
                    NodeBestBlockNumber = peerInfo.NumberAvailable,
                    OurBestBlockNumber = _blockTree.BestKnownNumber
                });

                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Starting sync processes with {peerInfo}, FullNodeId: {peerInfo.Peer.NodeId.PublicKey} " +
                        $"best known block #: {_blockTree.BestKnownNumber}, " +
                        $"best peer block #: {peerInfo.NumberAvailable}");

                var currentPeerNodeId = peerInfo.Peer?.NodeId;

                _peerSyncCancellationTokenSource = new CancellationTokenSource();
                var peerSynchronizationTask = SynchronizeWithPeerAsync(peerInfo);
                await peerSynchronizationTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError)
                        {
                            if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x is TimeoutException))
                            {
                                if (_logger.IsDebug) _logger.Debug($"Stopping sync with node: {currentPeerNodeId}. {t.Exception?.Message}");
                            }
                            else
                            {
                                _logger.Error($"Stopping sync with node: {currentPeerNodeId}. Error in the sync process.", t.Exception);
                            }
                        }

                        RemovePeer(peerInfo.Peer);
                        if (_logger.IsTrace) _logger.Trace($"Sync with Node: {currentPeerNodeId} failed. Removed node from sync peers.");
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Failed)
                        {
                            NodeBestBlockNumber = peerInfo.NumberAvailable,
                            OurBestBlockNumber = _blockTree.BestKnownNumber
                        });
                    }
                    else if (t.IsCanceled || _peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        if (_requestedSyncCancelDueToBetterPeer)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Cancelled sync with {_currentSyncingPeerInfo?.Peer.NodeId} due to connection with better peer.");
                            _requestedSyncCancelDueToBetterPeer = false;
                        }
                        else
                        {
                            RemovePeer(peerInfo.Peer);
                            if (_logger.IsTrace) _logger.Trace($"Sync with Node: {currentPeerNodeId} canceled. Removed node from sync peers.");
                            SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Cancelled)
                            {
                                NodeBestBlockNumber = peerInfo.NumberAvailable,
                                OurBestBlockNumber = _blockTree.BestKnownNumber
                            });
                        }
                    }
                    else if (t.IsCompleted)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Sync process finished with nodeId: {currentPeerNodeId}. Best known block is ({_blockTree.BestKnownNumber})");
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Completed)
                        {
                            NodeBestBlockNumber = peerInfo.NumberAvailable,
                            OurBestBlockNumber = _blockTree.BestKnownNumber
                        });
                    }

                    if (_logger.IsDebug)
                        _logger.Debug(
                            $"Finished peer sync process [{(t.IsFaulted ? "FAULTED" : t.IsCanceled ? "CANCELED" : t.IsCompleted ? "COMPLETED" : "OTHER")}] with Node: {peerInfo}], " +
                            $"best known block #: {_blockTree.BestKnownNumber} ({_blockTree.BestKnownNumber}), " +
                            $"best peer block #: {peerInfo.NumberAvailable} ({peerInfo.NumberAvailable})");

                    var source = _peerSyncCancellationTokenSource;
                    _peerSyncCancellationTokenSource = null;
                    source?.Dispose();
                }, syncCancellationToken);
            }
        }

        private PeerInfo SelectBestPeerForSync()
        {
            var availablePeers = _peers.Values.Where(x => x.NumberAvailable > _blockTree.BestKnownNumber).Where(x => x.IsInitialized).Select(x => new {PeerInfo = x, AvLat = x.Peer?.NodeStats?.GetAverageLatency(NodeLatencyStatType.BlockHeaders)})
                .OrderBy(x => x.AvLat ?? 100000).ToArray();
            if (!availablePeers.Any())
            {
                return null;
            }

            if (_logger.IsDebug) _logger.Debug($"Candidates for Sync: {Environment.NewLine}{string.Join(Environment.NewLine, availablePeers.Select(x => $"{x.PeerInfo.Peer.NodeId} | NumberAvailable: {x.PeerInfo.NumberAvailable} | BlockHeaderAvLatency: {x.AvLat?.ToString() ?? "none"}").ToArray())}");
            var selectedInfo = availablePeers.First().PeerInfo;
            if (selectedInfo.Peer.NodeId == _currentSyncingPeerInfo?.Peer?.NodeId)
            {
                if (_logger.IsDebug) _logger.Debug($"Potential error, selecting same peer for sync as prev sync peer, id: {selectedInfo.Peer.NodeId}");
            }

            return selectedInfo;
        }

        private UInt256 _lastSyncNumber = 0;

        [Todo(Improve.Readability, "Let us review the cancellation approach here")]
        private async Task SynchronizeWithPeerAsync(PeerInfo peerInfo)
        {
            bool wasCanceled = false;

            ISynchronizationPeer peer = peerInfo.Peer;
            UInt256 bestNumber = _blockTree.BestKnownNumber;
//            UInt256 bestDifficulty = _blockTree.BestSuggested.Difficulty;

            const int maxLookup = 2 * MaxBatchSize;
            int ancestorLookupLevel = 0;
            int emptyBlockListCounter = 0;
            bool isCommonAncestorKnown = false;

            peerInfo.NumberReceived = bestNumber;
            while (peerInfo.NumberAvailable > bestNumber && peerInfo.NumberReceived <= peerInfo.NumberAvailable)
            {
                if (_logger.IsTrace) _logger.Trace($"Continue syncing with {peerInfo} (our best {bestNumber})");

                if (ancestorLookupLevel > maxLookup)
                {
                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {peerInfo.Peer.NodeId}");
                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
                }

                if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"SOMEONE CANCELLED");
                    return;
                }

                if (!isCommonAncestorKnown)
                {
                    // TODO: cases when many peers used for sync and one peer finished sync and then we need resync - we should start from common point and not NumberReceived that may be far in the past
                    _logger.Trace($"Finding common ancestor for {peerInfo.Peer.NodeId}");
                    isCommonAncestorKnown = true;
                }

                UInt256 blocksLeft = peerInfo.NumberAvailable - peerInfo.NumberReceived;
                int blocksToRequest = (int) BigInteger.Min(blocksLeft + 1, _batchSize);
                if (_logger.IsTrace) _logger.Trace($"Sync request to peer with {peerInfo.NumberAvailable} blocks. Got {peerInfo.NumberReceived} and asking for {blocksToRequest} more.");

                Task<BlockHeader[]> headersTask = peer.GetBlockHeaders(peerInfo.NumberReceived, blocksToRequest, 0, _peerSyncCancellationTokenSource.Token);
                BlockHeader[] headers = await headersTask;
                if (headersTask.IsCanceled)
                {
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

                if (blocks.Length == 0 && ++emptyBlockListCounter == 10)
                {
                    if (_batchSize == MinBatchSize)
                    {
                        if (_logger.IsInfo) _logger.Info($"Received no blocks from {_currentSyncingPeerInfo} in response to {blocksToRequest} blocks requested. Cancelling.");
                        throw new EthSynchronizationException("Peer sent an empty block list");
                    }

                    if (_logger.IsInfo) _logger.Info($"Received no blocks from {_currentSyncingPeerInfo} in response to {blocksToRequest} blocks requested. Decreasing batch size from {_batchSize}.");
                    DecreaseBatchSize();
                    continue;
                }
                
                if (blocks.Length != 0)
                {
                    emptyBlockListCounter = 0;    
                }
                else
                {
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
                        ancestorLookupLevel += _batchSize;
                        peerInfo.NumberReceived = peerInfo.NumberReceived >= _batchSize ? (peerInfo.NumberReceived - (UInt256) _batchSize) : UInt256.Zero;
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
                        throw new EthSynchronizationException("Peer sent an inconsistent block list");
                    }
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received {blocks[i]} from {peer.NodeId}");

                    if (_blockValidator.ValidateSuggestedBlock(blocks[i]))
                    {
                        AddBlockResult addResult = _blockTree.SuggestBlock(blocks[i]);
                        if (addResult == AddBlockResult.UnknownParent)
                        {
                            if (_logger.IsTrace)
                                _logger.Trace($"Block {blocks[i].Number} ignored (unknown parent)");
                            if (i == 0)
                            {
                                const string message = "Peer sent orphaned blocks";
                                _logger.Error(message);
                                peerInfo.NumberReceived -= peerInfo.NumberReceived <= 60 ? UInt256.Zero : (UInt256) 60;
                                throw new EthSynchronizationException(message);
                            }
                            else
                            {
                                const string message = "Peer sent an inconsistent batch of block headers";
                                _logger.Error(message);
                                throw new EthSynchronizationException(message);
                            }
                        }

                        if (_logger.IsTrace) _logger.Trace($"Block {blocks[i].Number} suggested for processing");
                    }
                    else
                    {
                        if (_logger.IsWarn) _logger.Warn($"Block {blocks[i].Number} skipped (validation failed)");
                    }
                }

                peerInfo.NumberReceived = blocks[blocks.Length - 1].Number;
                bestNumber = _blockTree.BestKnownNumber;
                if (bestNumber > _lastSyncNumber + 10000)
                {
                    _lastSyncNumber = bestNumber;
                    if (_logger.IsDebug) _logger.Debug($"Downloading blocks. Current best at {_blockTree.BestKnownNumber}");
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Stopping sync processes with Node: {peerInfo.Peer.NodeId}, wasCancelled: {wasCanceled}");
        }

        [Todo(Improve.MissingFunctionality, "Eth63 / fast sync can download receipts using this method. Fast sync is not implemented although its methods and serializers are already written.")]
        private async Task<bool> DownloadReceipts(Block[] blocks, ISynchronizationPeer peer)
        {
            Block[] blocksWithTransactions = blocks.Where(b => b.Transactions.Length != 0).ToArray();
            if (blocksWithTransactions.Length != 0)
            {
                Task<TransactionReceipt[][]> receiptsTask = peer.GetReceipts(blocksWithTransactions.Select(b => b.Hash).ToArray(), _peerSyncCancellationTokenSource.Token);
                TransactionReceipt[][] receipts = await receiptsTask;
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
                        TransactionReceipt receipt = receipts[blockIndex][txIndex];
                        if (receipt == null)
                        {
                            throw new DataException($"Missing receipt for {blocksWithTransactions[blockIndex].Hash}->{txIndex}");
                        }

                        receipt.Index = txIndex;
                        receipt.BlockHash = blocksWithTransactions[blockIndex].Hash;
                        receipt.BlockNumber = blocksWithTransactions[blockIndex].Number;
                        receipt.TransactionHash = blocksWithTransactions[blockIndex].Transactions[txIndex].Hash;
                        gasUsedTotal += receipt.GasUsed;
                        receipt.GasUsedTotal = gasUsedTotal;
                        receipt.Recipient = blocksWithTransactions[blockIndex].Transactions[txIndex].To;

                        // only after execution
                        // receipt.Sender = blocksWithTransactions[blockIndex].Transactions[txIndex].SenderAddress; 
                        // receipt.Error = ...
                        // receipt.ContractAddress = ...

                        _receiptStorage.Add(receipt);
                    }
                }
            }

            return false;
        }

        private void IncreaseBatchSize()
        {
            _batchSize = Math.Min(MaxBatchSize, _batchSize * 2);
        }

        private void DecreaseBatchSize()
        {
            _batchSize = Math.Max(MinBatchSize, _batchSize / 2);
        }

        private async Task InitPeerInfo(ISynchronizationPeer peer, CancellationToken token)
        {
            if (_logger.IsTrace) _logger.Trace($"Requesting head block info from {peer.NodeId}");
            Task<Keccak> getHashTask = peer.GetHeadBlockHash(token);
            Task<UInt256> getNumberTask = peer.GetHeadBlockNumber(token);
//            Task<UInt256> getDifficultyTask = peer.GetHeadDifficulty(token);

            await Task.WhenAny(Task.WhenAll(getHashTask, getNumberTask), Task.Delay(10000, token)).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsTrace) _logger.Trace($"InitPeerInfo failed for node: {peer.NodeId}{Environment.NewLine}{t.Exception}");
                        RemovePeer(peer);
                        SyncEvent?.Invoke(this, new SyncEventArgs(peer, SyncStatus.InitFailed));
                    }
                    else if (t.IsCanceled)
                    {
                        RemovePeer(peer);
                        SyncEvent?.Invoke(this, new SyncEventArgs(peer, SyncStatus.InitCancelled));
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Received head block info from {peer.NodeId} with head block numer {getNumberTask.Result}");
                        SyncEvent?.Invoke(
                            this,
                            new SyncEventArgs(peer, SyncStatus.InitCompleted)
                            {
                                NodeBestBlockNumber = getNumberTask.Result,
                                OurBestBlockNumber = _blockTree.BestKnownNumber
                            });

                        bool result = _peers.TryGetValue(peer.NodeId, out PeerInfo peerInfo);
                        if (!result)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Initializing PeerInfo failed for {peer.NodeId}");
                            throw new EthSynchronizationException($"Initializing peer info failed for {peer.NodeId.ToString()}");
                        }

                        peerInfo.NumberAvailable = getNumberTask.Result;
//                        peerInfo.Difficulty = getDifficultyTask.Result;
                        peerInfo.NumberReceived = _blockTree.BestKnownNumber;
                        peerInfo.IsInitialized = true;
                    }
                }, token);
        }

        private class PeerInfo
        {
            public PeerInfo(ISynchronizationPeer peer)
            {
                Peer = peer;
            }

            public bool IsInitialized { get; set; }

            public ISynchronizationPeer Peer { get; }

//            public UInt256 Difficulty { get; set; }
            public UInt256 NumberAvailable { get; set; }
            public UInt256 NumberReceived { get; set; }

            public override string ToString()
            {
                return ToString(true);
            }

            public string ToString(bool fullFormat)
            {
                if (fullFormat)
                {
                    return $"[Peer|{Peer?.NodeId}|{NumberReceived}/{NumberAvailable}|{Peer?.ClientId}]";
                }

                return $"[Peer|{Peer?.NodeId}|{NumberReceived}/{NumberAvailable}]";
            }
        }
    }
}