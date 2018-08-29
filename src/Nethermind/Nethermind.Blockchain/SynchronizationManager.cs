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
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain
{
    // TODO: forks
    public class SynchronizationManager : ISynchronizationManager
    {
        public const int BatchSize = 8; // need to dynamically adjust - look at this with multiplexor

        private readonly ILogger _logger;
        private readonly IBlockValidator _blockValidator;
        private readonly IHeaderValidator _headerValidator;
        private readonly ConcurrentDictionary<NodeId, PeerInfo> _peers = new ConcurrentDictionary<NodeId, PeerInfo>();
        private readonly ConcurrentDictionary<NodeId, CancellationTokenSource> _initCancelTokens = new ConcurrentDictionary<NodeId, CancellationTokenSource>();

        private readonly ITransactionStore _transactionStore;
        private readonly ITransactionValidator _transactionValidator;
        private readonly IBlockchainConfig _blockchainConfig;
        private readonly IBlockTree _blockTree;

        private readonly object _isSyncingLock = new object();
        private bool _isSyncing;
        private bool _isInitialized;
        
        private PeerInfo _currentSyncingPeerInfo;
        private Task _currentSyncTask;

        private CancellationTokenSource _peerSyncCancellationTokenSource;
        private CancellationTokenSource _aggregateSyncCancellationTokenSource;

        public SynchronizationManager(
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            IHeaderValidator headerValidator,
            ITransactionStore transactionStore,
            ITransactionValidator transactionValidator,
            ILogManager logManager,
            IBlockchainConfig blockchainConfig)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockchainConfig = blockchainConfig ?? throw new ArgumentNullException(nameof(blockchainConfig));
            
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _transactionValidator = transactionValidator ?? throw new ArgumentNullException(nameof(transactionValidator));

            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));

            if (_logger.IsDebug) _logger.Debug($"Initialized {nameof(SynchronizationManager)} with head block {Head.ToString(BlockHeader.Format.Short)}");
            System.Timers.Timer syncTimer = new System.Timers.Timer(10000);
            syncTimer.Elapsed += (s, e) =>
            {
                if (_isInitialized) RequestSync();
            };
            
            syncTimer.Start();
        }

        public int ChainId => _blockTree.ChainId;
        public BlockHeader Genesis => _blockTree.Genesis;
        public BlockHeader Head => _blockTree.Head;
        public BigInteger HeadNumber => _blockTree.Head.Number;
        public BigInteger TotalDifficulty => _blockTree.Head?.TotalDifficulty ?? 0;
        public event EventHandler<SyncEventArgs> SyncEvent;

        public Block Find(Keccak hash)
        {
            return _blockTree.FindBlock(hash, false);
        }

        public Block Find(UInt256 number)
        {
            return _blockTree.FindBlock(number);
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

            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    if (_logger.IsTrace) _logger.Trace($"Ignoring new block {block.Hash} while syncing");
                    return;
                }
            }
            
            if (_logger.IsTrace) _logger.Trace($"Adding new block {block.Hash} ({block.Number}) from {receivedFrom}");
            
            if (block.Number <= _blockTree.BestSuggested.Number + 1)
            {
                if (_logger.IsTrace) _logger.Trace( $"Suggesting a block {block.Hash} ({block.Number}) from {receivedFrom} with {block.Transactions.Length} transactions");
                if (_logger.IsTrace) _logger.Trace($"{block}");

                AddBlockResult result = _blockTree.SuggestBlock(block);
                if (_logger.IsInfo) _logger.Info($"{block.Hash} ({block.Number}) adding result is {result}");
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

        private int _lastSyncPeersCount;

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
            _transactionStore.AddPending(transaction);
        }

        public async Task AddPeer(ISynchronizationPeer synchronizationPeer)
        {
            if (_logger.IsTrace) _logger.Trace($"Adding synchronization peer {synchronizationPeer.NodeId}");
            if (_peers.ContainsKey(synchronizationPeer.NodeId))
            {
                if (_logger.IsError) _logger.Error($"Sync peer already in peers collection: {synchronizationPeer.NodeId}");
                return;
            }

            _peers.TryAdd(synchronizationPeer.NodeId, new PeerInfo(synchronizationPeer));

            var tokenSource = _initCancelTokens[synchronizationPeer.NodeId] = new CancellationTokenSource();
            // ReSharper disable once MethodSupportsCancellation
            await InitPeerInfo(synchronizationPeer, tokenSource.Token).ContinueWith(t =>
            {
                _initCancelTokens.TryRemove(synchronizationPeer.NodeId, out _);
                if (t.IsFaulted)
                {
                    if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        if (_logger.IsInfo) _logger.Info($"AddPeer failed due to timeout: {t.Exception.Message}");
                    }
                    else if (_logger.IsError) _logger.Error("AddPeer failed.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsTrace) _logger.Trace($"Init peer info canceled: {synchronizationPeer.NodeId}");
                }
                else
                {
                    int initPeerCount = _peers.Count(p => p.Value.IsInitialized);
                    if (initPeerCount != _lastSyncPeersCount)
                    {
                        _lastSyncPeersCount = initPeerCount;
                        if (_logger.IsInfo) _logger.Info($"Available sync peers: {initPeerCount}/25"); // TODO: make 25 configurable
                    }
                    
                    RequestSync();
                }
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

            if (!_peers.TryRemove(synchronizationPeer.NodeId, out _))
            {
                //possible if sync failed - we remove peer and eventually initiate disconnect, which calls remove peer again
                return;
            }
            
            if(_currentSyncingPeerInfo?.Peer.NodeId.Equals(synchronizationPeer.NodeId) ?? false)
            {
                if (_logger.IsTrace) _logger.Trace($"Requesting peer cancel with: {synchronizationPeer.NodeId}");
                _peerSyncCancellationTokenSource?.Cancel();
            }

            if (_initCancelTokens.TryGetValue(synchronizationPeer.NodeId, out CancellationTokenSource tokenSource))
            {
                tokenSource.Cancel();
            }
        }

        public void Start()
        {
            _isInitialized = true;
            _blockTree.NewHeadBlock += OnNewHeadBlock;
            _transactionStore.NewPending += OnNewPendingTransaction;
        }

        public async Task StopAsync()
        {
            _isInitialized = false;
            if (_aggregateSyncCancellationTokenSource == null || _aggregateSyncCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            _aggregateSyncCancellationTokenSource?.Cancel();
            _peerSyncCancellationTokenSource?.Cancel();
            await (_currentSyncTask ?? Task.CompletedTask).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error($"StopAsync failed. Ex: {t.Exception}");
                }
            });
        }

        private void OnNewPendingTransaction(object sender, TransactionEventArgs transactionEventArgs)
        {
            /* This should be managed as part of the mempool.
             * Need to define strategy on which transactions to send and where to.
            */
            return;

            Transaction transaction = transactionEventArgs.Transaction;
            foreach ((NodeId nodeId, PeerInfo peerInfo) in _peers)
            {
                if (!(transaction.DeliveredBy?.Equals(nodeId.PublicKey) ?? false))
                {
                    peerInfo.Peer.SendNewTransaction(transaction);
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

            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    if (_logger.IsTrace) _logger.Trace("Sync in progress - skipping sync call");
                    return;
                }

                _isSyncing = true;
            }

            _aggregateSyncCancellationTokenSource = new CancellationTokenSource();
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
                PeerInfo peerInfo = _currentSyncingPeerInfo = _peers.Values.OrderBy(p => p.NumberAvailable).LastOrDefault();
                if (peerInfo == null)
                {
                    if (_logger.IsInfo) _logger.Info($"No sync peers available, finishing sync process, best known block #: {_blockTree.BestSuggested.Number}");
                    return;
                }

                if (syncCancellationToken.IsCancellationRequested)
                {
                    // it was throwing an exception here before, why? (tks 2018/08/24)
                    return;
                }

                if (_blockTree.BestSuggested.Number >= peerInfo.NumberAvailable)
                {
                    if (_logger.IsInfo) _logger.Info(
                        "No more peers with higher block number available, finishing sync process, " +
                        $"best known block #: {_blockTree.BestSuggested.Number}, " +
                        $"best peer block #: {peerInfo.NumberAvailable}");
                    return;
                }

                SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Started)
                {
                    NodeBestBlockNumber = peerInfo.NumberAvailable,
                    OurBestBlockNumber = _blockTree.BestSuggested.Number
                });

                if (_logger.IsInfo) _logger.Info(
                    $"Starting sync processes with Node: {peerInfo.Peer.NodeId} [{peerInfo.Peer.ClientId}], " +
                    $"best known block #: {_blockTree.BestSuggested.Number}, " + 
                    $"best peer block #: {peerInfo.NumberAvailable}");

                var currentPeerNodeId = peerInfo.Peer?.NodeId;

                _peerSyncCancellationTokenSource = new CancellationTokenSource();
                var peerSynchronizationTask = SynchronizeWithPeerAsync(peerInfo, _peerSyncCancellationTokenSource.Token);
                await peerSynchronizationTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError)
                        {
                            if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x is TimeoutException))
                            {
                                if (_logger.IsWarn) _logger.Warn($"Stopping Sync with node: {currentPeerNodeId}. {t.Exception?.Message}");
                            }
                            else
                            {
                                _logger.Error($"Stopping Sync with node: {currentPeerNodeId}. Error in the sync process: {t.Exception?.Message}");
                            }
                        }

                        RemovePeer(peerInfo.Peer);
                        if (_logger.IsTrace) _logger.Trace($"Sync with Node: {currentPeerNodeId} failed. Removed node from sync peers.");
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Failed)
                        {
                            NodeBestBlockNumber = peerInfo.NumberAvailable,
                            OurBestBlockNumber = _blockTree.BestSuggested.Number
                        });
                    }
                    else if (t.IsCanceled)
                    {
                        RemovePeer(peerInfo.Peer);
                        if (_logger.IsTrace) _logger.Trace($"Sync with Node: {currentPeerNodeId} canceled. Removed node from sync peers.");
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Cancelled)
                        {
                            NodeBestBlockNumber = peerInfo.NumberAvailable,
                            OurBestBlockNumber = _blockTree.BestSuggested.Number
                        });
                    }
                    else if (t.IsCompleted)
                    {
                        if (_logger.IsInfo) _logger.Info($"Sync process finished with nodeId: {currentPeerNodeId}. Best block now is {_blockTree.BestSuggested.Hash} ({_blockTree.BestSuggested.Number})");
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Completed)
                        {
                            NodeBestBlockNumber = peerInfo.NumberAvailable,
                            OurBestBlockNumber = _blockTree.BestSuggested.Number
                        });
                    }

                    if (_logger.IsInfo) _logger.Info(
                        $"Finished peer sync process [{(t.IsFaulted ? "FAULTED" : t.IsCanceled ? "CANCELED" : t.IsCompleted ? "COMPLETED" : "OTHER")}] with Node: {peerInfo.Peer.NodeId} [{peerInfo.Peer.ClientId}], " +
                        $"peer highest block #: {peerInfo.NumberAvailable}, " +
                        $"our highest block #: {_blockTree.BestSuggested.Number}");
                }, syncCancellationToken);
            }
        }

        private async Task SynchronizeWithPeerAsync(PeerInfo peerInfo, CancellationToken peerSyncToken)
        {
            bool wasCanceled = false;

            ISynchronizationPeer peer = peerInfo.Peer;
            BigInteger bestNumber = _blockTree.BestSuggested.Number;

            const int maxLookup = 64;
            int ancestorLookupLevel = 0;
            bool isCommonAncestorKnown = false;

            while (peerInfo.NumberAvailable > bestNumber && peerInfo.NumberReceived <= peerInfo.NumberAvailable)
            {
                if (_logger.IsTrace) _logger.Trace($"Continue syncing with {peerInfo} (our best {bestNumber})");

                if (ancestorLookupLevel > maxLookup)
                {
                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {peerInfo.Peer.NodeId}");
                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
                }

                if (peerSyncToken.IsCancellationRequested)
                {
                    peerSyncToken.ThrowIfCancellationRequested();
                }

                if (!isCommonAncestorKnown)
                {
                    // TODO: cases when many peers used for sync and one peer finished sync and then we need resync - we should start from common point and not NumberReceived that may be far in the past
                    _logger.Trace($"Finding common ancestor for {peerInfo.Peer.NodeId}");
                    isCommonAncestorKnown = true;
                }

                BigInteger blocksLeft = peerInfo.NumberAvailable - peerInfo.NumberReceived;
                int blocksToRequest = (int) BigInteger.Min(blocksLeft + 1, BatchSize);
                if (_logger.IsTrace) _logger.Trace($"Sync request to peer with {peerInfo.NumberAvailable} blocks. Got {peerInfo.NumberReceived} and asking for {blocksToRequest} more.");
                
                Task<BlockHeader[]> headersTask = peer.GetBlockHeaders(peerInfo.NumberReceived, blocksToRequest, 0, peerSyncToken);
                _currentSyncTask = headersTask;
                BlockHeader[] headers = await headersTask;
                if (_currentSyncTask.IsCanceled)
                {
                    wasCanceled = true;
                    break;
                }

                if (_currentSyncTask.IsFaulted)
                {
                    if (_currentSyncTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve headers when synchronizing (Timeout)", _currentSyncTask.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve headers when synchronizing", _currentSyncTask.Exception);
                    }
                    throw _currentSyncTask.Exception;
                }

                if (peerSyncToken.IsCancellationRequested)
                {
                    peerSyncToken.ThrowIfCancellationRequested();
                }

                List<Keccak> hashes = new List<Keccak>();
                Dictionary<Keccak, BlockHeader> headersByHash = new Dictionary<Keccak, BlockHeader>();
                for (int i = 1; i < headers.Length; i++)
                {
                    hashes.Add(headers[i].Hash);
                    headersByHash[headers[i].Hash] = headers[i];
                }

                Task<Block[]> bodiesTask = peer.GetBlocks(hashes.ToArray(), peerSyncToken);
                _currentSyncTask = bodiesTask;
                Block[] blocks = await bodiesTask;
                if (_currentSyncTask.IsCanceled)
                {
                    wasCanceled = true;
                    break;
                }

                if (_currentSyncTask.IsFaulted)
                {
                    if (_currentSyncTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve bodies when synchronizing (Timeout)", _currentSyncTask.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve bodies when synchronizing", _currentSyncTask.Exception);
                    }
                    throw _currentSyncTask.Exception;
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (peerSyncToken.IsCancellationRequested)
                    {
                        peerSyncToken.ThrowIfCancellationRequested();
                    }

                    blocks[i].Header = headersByHash[hashes[i]];
                }

                if (blocks.Length > 0)
                {
                    Block parent = _blockTree.FindParent(blocks[0]);
                    if (parent == null)
                    {
                        ancestorLookupLevel += BatchSize;
                        peerInfo.NumberReceived = peerInfo.NumberReceived >= BatchSize ? peerInfo.NumberReceived - BatchSize : 0;
                        continue;
                    }
                }

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
                                if (_logger.IsTrace) _logger.Trace("Resyncing split");
                                peerInfo.NumberReceived -= 1;
                                var syncTask =
                                    Task.Run(() => SynchronizeWithPeerAsync(peerInfo, _peerSyncCancellationTokenSource.Token),
                                        _peerSyncCancellationTokenSource.Token);
                                await syncTask;
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
                bestNumber = _blockTree.BestSuggested.Number;
            }

            if (_logger.IsTrace) _logger.Trace($"Stopping sync processes with Node: {peerInfo.Peer.NodeId}, wasCancelled: {wasCanceled}");
        }

        private async Task InitPeerInfo(ISynchronizationPeer peer, CancellationToken token)
        {
            if (_logger.IsTrace) _logger.Trace($"Requesting head block info from {peer.NodeId}");
            Task<Keccak> getHashTask = peer.GetHeadBlockHash();
            Task<UInt256> getNumberTask = peer.GetHeadBlockNumber(token);

            await Task.WhenAll(getHashTask, getNumberTask).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsTrace) _logger.Trace($"{nameof(InitPeerInfo)} failed for node: {peer.NodeId}{Environment.NewLine}{t.Exception}");
                        SyncEvent?.Invoke(this, new SyncEventArgs(peer, SyncStatus.InitFailed));
                    }
                    else if (t.IsCanceled)
                    {
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
                                OurBestBlockNumber = _blockTree.BestSuggested.Number
                            });

                        bool result = _peers.TryGetValue(peer.NodeId, out PeerInfo peerInfo);
                        if (!result)
                        {
                            _logger.Error($"Initializing PeerInfo failed for {peer.NodeId}");
                            throw new EthSynchronizationException($"Initializing peer info failed for {peer.NodeId.ToString()}");
                        }
                        
                        peerInfo.NumberAvailable = getNumberTask.Result;
                        peerInfo.NumberReceived = _blockTree.BestSuggested.Number;
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
            public UInt256 NumberAvailable { get; set; }
            public UInt256 NumberReceived { get; set; }

            public override string ToString()
            {
                return $"Peer {Peer.NodeId}, Available {NumberAvailable}, Received {NumberReceived}";
            }
        }
    }
}