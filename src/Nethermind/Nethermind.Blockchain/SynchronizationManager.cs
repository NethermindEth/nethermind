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
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
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
        public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(10);

        public const int
            BatchSize = 8; // when syncing, we got disconnected on 16 because of the too big Snappy message, need to dynamically adjust

        private readonly IBlockValidator _blockValidator;
        private readonly IHeaderValidator _headerValidator;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<NodeId, PeerInfo> _peers = new ConcurrentDictionary<NodeId, PeerInfo>();

        private readonly ConcurrentDictionary<NodeId, NodeId> _initPeersInProgress =
            new ConcurrentDictionary<NodeId, NodeId>();

        private readonly ITransactionStore _transactionStore;
        private readonly ITransactionValidator _transactionValidator;
        private readonly IBlockchainConfig _blockchainConfig;
        private Task _currentSyncTask;
        private bool _isSyncing;
        private PeerInfo _currentSyncingPeer;
        private bool _isInitialized;

        private CancellationTokenSource _peerSyncCancellationTokenSource;
        private CancellationTokenSource _aggregateSyncCancellationTokenSource;

        private readonly ConcurrentDictionary<NodeId, CancellationTokenSource> _initCancellationTokenSources =
            new ConcurrentDictionary<NodeId, CancellationTokenSource>();

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
            _blockchainConfig = blockchainConfig;
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _transactionValidator =
                transactionValidator ?? throw new ArgumentNullException(nameof(transactionValidator));

            BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));

            _logger.Debug(
                $"Initialized {nameof(SynchronizationManager)} with head block {Head.ToString(BlockHeader.Format.Short)}");
        }

        public int ChainId => BlockTree.ChainId;
        public BlockHeader Genesis => BlockTree.Genesis;
        public BlockHeader Head => BlockTree.Head;
        public BigInteger HeadNumber => BlockTree.Head.Number;
        public BigInteger TotalDifficulty => BlockTree.Head?.TotalDifficulty ?? 0;
        public IBlockTree BlockTree { get; set; }
        public event EventHandler<SyncEventArgs> SyncEvent;

        public Block Find(Keccak hash)
        {
            return BlockTree.FindBlock(hash, false);
        }

        public Block[] Find(Keccak hash, int numberOfBlocks, int skip, bool reverse)
        {
            return BlockTree.FindBlocks(hash, numberOfBlocks, skip, reverse);
        }

        public Block Find(UInt256 number)
        {
            return BlockTree.FindBlock(number);
        }

        public void AddNewBlock(Block block, NodeId receivedFrom)
        {
            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    if (_logger.IsDebugEnabled) _logger.Debug($"Ignoring new block {block.Hash} while syncing");
                    return;
                }
            }

            if (!BlockTree.CanAcceptNewBlocks)
            {
                if (_logger.IsDebugEnabled) _logger.Debug($"Ignoring new block {block.Hash} while block tree not ready.");
                return;
            }

            // TODO: validation

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Adding new block {block.Hash} ({block.Number}) from {receivedFrom}");
            }

            bool getValueResult = _peers.TryGetValue(receivedFrom, out PeerInfo peerInfo);
            if (!getValueResult)
            {
                _logger.Error($"Try get value failed on {nameof(PeerInfo)} {receivedFrom}");
            }

            if (peerInfo == null && _logger.IsErrorEnabled)
            {
                _logger.Error($"{receivedFrom} peer info is null");
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Using peer:{peerInfo} when adding new block");
            }

            if (peerInfo == null)
            {
                string errorMessage = $"unknown synchronization peer {receivedFrom}";
                _logger.Error(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }

            peerInfo.NumberAvailable = UInt256.Max(block.Number, peerInfo.NumberAvailable);

            if (block.Number <= BlockTree.BestSuggested.Number + 1)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug(
                        $"Suggesting a block {block.Hash} ({block.Number}) from {receivedFrom} with {block.Transactions.Length} transactions");
                }

                if (_logger.IsTraceEnabled)
                {
                    _logger.Trace("SUGGESTING BLOCK:");
                    _logger.Trace($"{block}");
                }

                AddBlockResult result = BlockTree.SuggestBlock(block);
                if (result == AddBlockResult.UnknownParent)
                {
                    RunSync();
                }
                else
                {
                    peerInfo.NumberReceived = block.Number;
                }

                if (_logger.IsInfoEnabled) _logger.Info($"{block.Hash} ({block.Number}) adding result is {result}");
            }
            else if (block.Number > BlockTree.BestSuggested.Number + 1)
            {
                if (_logger.IsDebugEnabled)
                    _logger.Debug(
                        $"Received a block {block.Hash} ({block.Number}) from {receivedFrom} - need to resync");
                RunSync();
            }
            else
            {
                Debug.Fail("above should be covering everything");
            }
        }

        public void HintBlock(Keccak hash, UInt256 number, NodeId receivedFrom)
        {
            if (!_peers.TryGetValue(receivedFrom, out PeerInfo peerInfo))
            {
                string message = $"Received a block hint from an unknown peer {receivedFrom}";
                if(_logger.IsDebugEnabled) _logger.Debug(message);
                return;
            }

            peerInfo.NumberAvailable = UInt256.Max(number, peerInfo.NumberAvailable);
            // TODO: sync?
        }

        public void AddNewTransaction(Transaction transaction, NodeId receivedFrom)
        {
            if (_logger.IsTraceEnabled) _logger.Trace($"Received a pending transaction {transaction.Hash} from {receivedFrom}");
            _transactionStore.AddPending(transaction);
            // TODO: reputation
        }

        public async Task AddPeer(ISynchronizationPeer synchronizationPeer)
        {
            if (!_isInitialized)
            {
                if (_logger.IsDebugEnabled) _logger.Debug($"Synchronization is disabled, adding peer is blocked: {synchronizationPeer.NodeId}");
                return;
            }

            if (!_initPeersInProgress.TryAdd(synchronizationPeer.NodeId, synchronizationPeer.NodeId))
            {
                if (_logger.IsDebugEnabled) _logger.Debug($"Another sync init in progress, adding peer is blocked: {synchronizationPeer.NodeId}");
                return;
            }

            if (_peers.ContainsKey(synchronizationPeer.NodeId))
            {
                if (_logger.IsDebugEnabled) _logger.Debug($"Sync peer already in peers collection: {synchronizationPeer.NodeId}");
                return;
            }

            if (_logger.IsDebugEnabled) _logger.Debug($"Adding synchronization peer {synchronizationPeer.NodeId}");

            var tokenSource = _initCancellationTokenSources[synchronizationPeer.NodeId] = new CancellationTokenSource();

            await InitPeerInfo(synchronizationPeer, tokenSource.Token).ContinueWith(t =>
            {
                _initCancellationTokenSources.TryRemove(synchronizationPeer.NodeId, out _);
                _initPeersInProgress.TryRemove(synchronizationPeer.NodeId, out _);

                if (t.IsFaulted)
                {
                    if (t.Exception != null &&
                        t.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        if (_logger.IsWarnEnabled)
                            _logger.Warn($"AddPeer failed due to timeout: {t.Exception.Message}");
                    }
                    else
                    {
                        if (_logger.IsErrorEnabled) _logger.Error("AddPeer failed.", t.Exception);
                    }
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsWarnEnabled) _logger.Warn($"Init peer info cancelled: {synchronizationPeer.NodeId}");
                }
                else if (BlockTree.CanAcceptNewBlocks)
                {
                    if (!BlockTree.CanAcceptNewBlocks)
                    {
                        if (_logger.IsDebugEnabled) _logger.Debug("Ignoring new block peer while block tree not ready.");
                        return;
                    }
                    
                    RunSync();
                }
            }, tokenSource.Token);
        }

        public void RemovePeer(ISynchronizationPeer synchronizationPeer)
        {
            if (!_isInitialized)
            {
                if (_logger.IsDebugEnabled) _logger.Debug($"Synchronization is disabled, removing peer is blocked: {synchronizationPeer.NodeId}");
                return;
            }

            if (!_peers.TryRemove(synchronizationPeer.NodeId, out var _))
            {
                return;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Removing synchronization peer {synchronizationPeer.NodeId}");
            }

            lock (_isSyncingLock)
            {
                if (_isSyncing && (_currentSyncingPeer?.Peer.NodeId.Equals(synchronizationPeer.NodeId) ?? false))
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"Requesting peer cancel with: {synchronizationPeer.NodeId}");
                    }

                    _peerSyncCancellationTokenSource?.Cancel();
                }
            }

            if (_initCancellationTokenSources.TryGetValue(synchronizationPeer.NodeId, out var tokenSource))
            {
                tokenSource.Cancel();
            }
        }

        public void Start()
        {
            _isInitialized = true;
            BlockTree.NewHeadBlock += OnNewHeadBlock;
            _transactionStore.NewPending += OnNewPendingTransaction;
        }

        public async Task StopAsync()
        {
            _isInitialized = false;
            if (_aggregateSyncCancellationTokenSource == null ||
                _aggregateSyncCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            _aggregateSyncCancellationTokenSource?.Cancel();
            _peerSyncCancellationTokenSource?.Cancel();
            await (_currentSyncTask ?? Task.CompletedTask).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"{nameof(StopAsync)} failed. Ex: {t.Exception}");
                    }
                }
            });
        }

        private void OnNewPendingTransaction(object sender, TransactionEventArgs transactionEventArgs)
        {
            if (_isSyncing)
            {
                return;
            }

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

        private readonly object _isSyncingLock = new object();

        private void RunSync()
        {
            if (_logger.IsDebugEnabled) _logger.Debug($"Starting chain synchronization from {BlockTree.BestSuggested}");

            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    if (_logger.IsDebugEnabled) _logger.Debug("Sync in progress - skipping sync call");
                    return;
                }

                _isSyncing = true;

                if (_logger.IsDebugEnabled) _logger.Debug("Starting aggregate sync");
            }

            _aggregateSyncCancellationTokenSource = new CancellationTokenSource();
            var syncTask = Task.Run(() => SyncAsync(_aggregateSyncCancellationTokenSource.Token),
                _aggregateSyncCancellationTokenSource.Token);
            syncTask.ContinueWith(t =>
            {
                lock (_isSyncingLock)
                {
                    _isSyncing = false;
                }

                if (t.IsFaulted)
                {
                    if (_logger.IsErrorEnabled)
                    {
                        _logger.Error($"Error during aggregate sync process: {t.Exception}");
                    }
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info("Aggregate sync process cancelled");
                    }
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info("Aggregate sync process finished");
                    }
                }

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info(
                        $"Aggregate sync process finished, [{(t.IsFaulted ? "FAULTED" : t.IsCanceled ? "CANCELLED" : t.IsCompleted ? "COMPLETED" : "OTHER")}]");
                }
            });
        }

        private async Task SyncAsync(CancellationToken aggregateToken)
        {
            while (true)
            {
                // TODO: order by total difficulty
                var peers = _peers.Where(x => !x.Value.IsSynced).OrderBy(p => p.Value.NumberAvailable).ToArray();

                if (!peers.Any())
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info(
                            $"No sync peers availible, finishing sync process, our block #: {BlockTree.BestSuggested.Number}");
                    }

                    return;
                }

                if (aggregateToken.IsCancellationRequested)
                {
                    aggregateToken.ThrowIfCancellationRequested();
                }

                var peerInfo = peers.Last().Value;

                if (BlockTree.BestSuggested.Number >= peerInfo.NumberAvailable)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info(
                            "No more peers with higher block number availible, finishing sync process, " +
                            $"our highest block #: {BlockTree.BestSuggested.Number}, " +
                            $"highest peer block #: {peerInfo.NumberAvailable}");
                    return;
                }

                _currentSyncingPeer = peerInfo;
                SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Started)
                {
                    NodeBestBlockNumber = peerInfo.NumberAvailable,
                    OurBestBlockNumber = BlockTree.BestSuggested.Number
                });
                
                if (_logger.IsInfoEnabled)
                    _logger.Info(
                        $"Starting sync processes with Node: {peerInfo.Peer.NodeId} [{peerInfo.Peer.ClientId}], " +
                        $"peer highest block #: {peerInfo.NumberAvailable}, " +
                        $"our highest block #: {BlockTree.BestSuggested.Number}");

                _peerSyncCancellationTokenSource = new CancellationTokenSource();

                var currentPeerNodeId = peerInfo.Peer?.NodeId;

                var syncTask = PeerSyncAsync(_peerSyncCancellationTokenSource.Token, peerInfo);
                await syncTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsErrorEnabled)
                        {
                            if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x is TimeoutException))
                            {
                                if(_logger.IsWarnEnabled) _logger.Warn($"Stopping Sync with node: {currentPeerNodeId}. " +
                                             $"{t.Exception?.Message}");
                            }
                            else
                            {
                                _logger.Error($"Stopping Sync with node: {currentPeerNodeId}. " +
                                              $"Error in the sync process: {t.Exception?.Message}");
                            }
                        }
                        else if (_logger.IsDebugEnabled)
                        {
                            _logger.Error($"Stopping Sync with node: {currentPeerNodeId}. Error in the sync process", t.Exception);
                        }

                        //TODO check if we should disconnect this node
                        RemovePeer(peerInfo.Peer);
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"Sync with Node: {currentPeerNodeId} failed. Removed node from sync peers.");
                        }

                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Failed)
                        {
                            NodeBestBlockNumber = peerInfo.NumberAvailable,
                            OurBestBlockNumber = BlockTree.BestSuggested.Number
                        });
                    }
                    else if (t.IsCanceled)
                    {
                        RemovePeer(peerInfo.Peer);
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug(
                                $"Sync with Node: {currentPeerNodeId} canceled. Removed node from sync peers.");
                        }

                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Cancelled)
                        {
                            NodeBestBlockNumber = peerInfo.NumberAvailable,
                            OurBestBlockNumber = BlockTree.BestSuggested.Number
                        });
                    }
                    else if (t.IsCompleted)
                    {
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info(
                                $"Sync process finished with nodeId: {currentPeerNodeId}. Best block now is {BlockTree.BestSuggested.Hash} ({BlockTree.BestSuggested.Number})");
                        }

                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Completed)
                        {
                            NodeBestBlockNumber = peerInfo.NumberAvailable,
                            OurBestBlockNumber = BlockTree.BestSuggested.Number
                        });
                    }

                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info(
                            $"Finished sync process [{(t.IsFaulted ? "FAULTED" : t.IsCanceled ? "CANCELLED" : t.IsCompleted ? "COMPLETED" : "OTHER")}] with Node: {peerInfo.Peer.NodeId} [{peerInfo.Peer.ClientId}], " +
                            $"peer highest block #: {peerInfo.NumberAvailable}, " +
                            $"our highest block #: {BlockTree.BestSuggested.Number}");
                    }
                }, aggregateToken);
            }
        }

        private async Task PeerSyncAsync(CancellationToken token, PeerInfo peerInfo)
        {
            bool wasCancelled = false;

            ISynchronizationPeer peer = peerInfo.Peer;
            BigInteger bestNumber = BlockTree.BestSuggested.Number;

            const int maxLookup = 64;
            int ancestorLookupLevel = 0;
            bool isCommonAncestorKnown = false;

            while (peerInfo.NumberAvailable > bestNumber && peerInfo.NumberReceived <= peerInfo.NumberAvailable)
            {
                if (_logger.IsDebugEnabled) _logger.Debug($"Continue syncing with {peerInfo} (our best {bestNumber})");

                if (ancestorLookupLevel > maxLookup)
                {
                    throw new InvalidOperationException(
                        "Cannot find ancestor"); // TODO: remodel this after full sync test is added
                }

                if (token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                }

                if (!isCommonAncestorKnown)
                {
                    // TODO: cases when many peers used for sync and one peer finished sync and then we need resync - we should start from common point and not NumberReceived that may be far in the past
                    _logger.Debug($"Finding common ancestor for {peerInfo.Peer.NodeId}");
                    isCommonAncestorKnown = true;
                }

                BigInteger blocksLeft = peerInfo.NumberAvailable - peerInfo.NumberReceived;
                // TODO: fault handling on tasks

                int blocksToRequest = (int) BigInteger.Min(blocksLeft + 1, BatchSize);
                if (_logger.IsDebugEnabled)
                    _logger.Debug(
                        $"Sync request to peer with {peerInfo.NumberAvailable} blocks. Got {peerInfo.NumberReceived} and asking for {blocksToRequest} more.");
                Task<BlockHeader[]> headersTask =
                    peer.GetBlockHeaders(peerInfo.NumberReceived, blocksToRequest, 0, token);
                _currentSyncTask = headersTask;
                BlockHeader[] headers = await headersTask;
                if (_currentSyncTask.IsCanceled)
                {
                    wasCancelled = true;
                    break;
                }

                if (_currentSyncTask.IsFaulted)
                {
                    _logger.Error("Failed to retrieve headers when synchronizing", _currentSyncTask.Exception);
                    throw _currentSyncTask.Exception;
                }

                if (token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                }

                List<Keccak> hashes = new List<Keccak>();
                Dictionary<Keccak, BlockHeader> headersByHash = new Dictionary<Keccak, BlockHeader>();
                for (int i = 1; i < headers.Length; i++)
                {
                    hashes.Add(headers[i].Hash);
                    headersByHash[headers[i].Hash] = headers[i];
                }

                Task<Block[]> bodiesTask = peer.GetBlocks(hashes.ToArray(), token);
                _currentSyncTask = bodiesTask;
                Block[] blocks = await bodiesTask;
                if (_currentSyncTask.IsCanceled)
                {
                    wasCancelled = true;
                    break;
                }

                if (_currentSyncTask.IsFaulted)
                {
                    _logger.Error("Failed to retrieve bodies when synchronizing", _currentSyncTask.Exception);
                    throw _currentSyncTask.Exception;
                }

                ancestorLookupLevel = 0;

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (token.IsCancellationRequested)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    blocks[i].Header = headersByHash[hashes[i]];
                }

                if (blocks.Length > 0)
                {
                    Block parent = BlockTree.FindParent(blocks[0]);
                    if (parent == null)
                    {
                        ancestorLookupLevel += BatchSize;
                        peerInfo.NumberReceived -= BatchSize;
                        continue;
                    }
                }

                // Parity 1.11 non canonical blocks when testing on 27/06
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (i != 0 && blocks[i].ParentHash != blocks[i - 1].Hash)
                    {
                        throw new EthSynchronizationException("Peer send an inconsistent block list");
                    }
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (_logger.IsDebugEnabled) _logger.Debug($"Received {blocks[i]} from {peer.NodeId}");

                    if (_blockValidator.ValidateSuggestedBlock(blocks[i]))
                    {
                        AddBlockResult addResult = BlockTree.SuggestBlock(blocks[i]);
                        if (addResult == AddBlockResult.UnknownParent)
                        {
                            if (_logger.IsDebugEnabled)
                                _logger.Debug($"Block {blocks[i].Number} ignored (unknown parent)");
                            if (i == 0)
                            {
                                if (_logger.IsDebugEnabled) _logger.Debug("Resyncing split");
                                peerInfo.NumberReceived -= 1;
                                var syncTask =
                                    Task.Run(() => PeerSyncAsync(_peerSyncCancellationTokenSource.Token, peerInfo),
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

                        if (_logger.IsTraceEnabled) _logger.Trace($"Block {blocks[i].Number} suggested for processing");
                    }
                    else
                    {
                        if (_logger.IsWarnEnabled)
                            _logger.Warn($"Block {blocks[i].Number} skipped (validation failed)");
                    }
                }

                peerInfo.NumberReceived = blocks[blocks.Length - 1].Number;

                bestNumber = BlockTree.BestSuggested.Number;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug(
                    $"Stopping sync processes with Node: {peerInfo.Peer.NodeId}, wasCancelled: {wasCancelled}");
            }

            if (!wasCancelled)
            {
                peerInfo.IsSynced = true;
                //Synced?.Invoke(this, new SyncEventArgs(peerInfo.Peer, SyncStatus.Completed));
            }
        }

        private async Task InitPeerInfo(ISynchronizationPeer peer, CancellationToken token)
        {
            Task<Keccak> getHashTask = peer.GetHeadBlockHash();

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Requesting head block info from {peer.NodeId}");
            }

            Task<UInt256> getNumberTask = peer.GetHeadBlockNumber(token);
            await Task.WhenAll(getHashTask, getNumberTask).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsWarnEnabled)
                        {
                            _logger.Warn($"InitPeerInfo failed for node: {peer.NodeId}, Exp: {t?.Exception.Message}");
                        }

                        SyncEvent?.Invoke(this, new SyncEventArgs(peer, SyncStatus.InitFailed));
                        return;
                    }

                    if (t.IsCanceled)
                    {
                        SyncEvent?.Invoke(this, new SyncEventArgs(peer, SyncStatus.InitCancelled));
                        token.ThrowIfCancellationRequested();
                    }
                }, token);

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug(
                    $"Received head block info from {peer.NodeId} with head block numer {getNumberTask.Result}");
            }

            SyncEvent?.Invoke(this, new SyncEventArgs(peer, SyncStatus.InitCompleted)
            {
                NodeBestBlockNumber = getNumberTask.Result,
                OurBestBlockNumber = BlockTree?.BestSuggested.Number
            });

            bool addResult = _peers.TryAdd(peer.NodeId,
                new PeerInfo(peer, getNumberTask.Result)
                {
                    NumberReceived = BlockTree.BestSuggested.Number
                }); // TODO: cheating now with assumign the consistency of the chains
            if (!addResult)
            {
                _logger.Error($"Adding PeerInfo failed for {peer.NodeId}");
            }
        }

        private class PeerInfo
        {
            public PeerInfo(ISynchronizationPeer peer, UInt256 bestRemoteBlockNumber)
            {
                Peer = peer;
                NumberAvailable = bestRemoteBlockNumber;
            }

            public ISynchronizationPeer Peer { get; }
            public UInt256 NumberAvailable { get; set; }
            public UInt256 NumberReceived { get; set; }
            public bool IsSynced { get; set; }

            public override string ToString()
            {
                return
                    $"Peer {Peer.NodeId}, Available {NumberAvailable}, Received {NumberReceived}, Is Synced: {IsSynced}";
            }
        }
    }
}