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

namespace Nethermind.Blockchain
{
    // TODO: forks
    public class SynchronizationManager : ISynchronizationManager
    {
        public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(10);

        public const int BatchSize = 8; // when syncing, we got disconnected on 16 because of the too big Snappy message, need to dynamically adjust
        private readonly IBlockValidator _blockValidator;
        private readonly IHeaderValidator _headerValidator;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<NodeId, PeerInfo> _peers = new ConcurrentDictionary<NodeId, PeerInfo>();
        private readonly ITransactionStore _transactionStore;
        private readonly ITransactionValidator _transactionValidator;
        private readonly IBlockchainConfig _blockchainConfig;
        private Task _currentSyncTask;
        private bool _isSyncing;
        private PeerInfo _currentSyncingPeer;
        //private System.Timers.Timer _syncTimer;

        private CancellationTokenSource _syncCancellationTokenSource;
        private ConcurrentDictionary<NodeId, CancellationTokenSource> _initCancellationTokenSources = new ConcurrentDictionary<NodeId, CancellationTokenSource>();

        public SynchronizationManager(
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            IHeaderValidator headerValidator,
            ITransactionStore transactionStore,
            ITransactionValidator transactionValidator,
            ILogger logger, IBlockchainConfig blockchainConfig)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blockchainConfig = blockchainConfig;
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _transactionValidator = transactionValidator ?? throw new ArgumentNullException(nameof(transactionValidator));

            BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
            BlockTree.NewHeadBlock += OnNewHeadBlock;
            _transactionStore.NewPending += OnNewPendingTransaction;

            _logger.Info($"Initialized {nameof(SynchronizationManager)} with head block {Head.ToString(BlockHeader.Format.Short)}");
        }

        public int ChainId => BlockTree.ChainId;
        public BlockHeader Genesis => BlockTree.Genesis;
        public BlockHeader Head => BlockTree.Head;
        public BigInteger HeadNumber => BlockTree.Head.Number;
        public BigInteger TotalDifficulty => BlockTree.Head?.TotalDifficulty ?? 0;
        public IBlockTree BlockTree { get; set; }

        public Block Find(Keccak hash)
        {
            return BlockTree.FindBlock(hash, false);
        }

        public Block[] Find(Keccak hash, int numberOfBlocks, int skip, bool reverse)
        {
            return BlockTree.FindBlocks(hash, numberOfBlocks, skip, reverse);
        }

        public Block Find(BigInteger number)
        {
            return BlockTree.FindBlock(number);
        }

        public void AddNewBlock(Block block, NodeId receivedFrom)
        {
            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    _logger.Info($"Ignoring new block {block.Hash} while syncing");
                    return;
                }
            }

            // TODO: validation

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Adding new block {block.Hash} ({block.Number}) from {receivedFrom}");
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
                _logger.Debug($"Using {peerInfo}");
            }

            if (peerInfo == null)
            {
                string errorMessage = $"unknown synchronization peer {receivedFrom}";
                _logger.Error(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }

            peerInfo.NumberAvailable = BigInteger.Max(block.Number, peerInfo.NumberAvailable);

            if (block.Number <= BlockTree.BestSuggested.Number + 1)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Suggesting a block {block.Hash} ({block.Number}) from {receivedFrom} with {block.Transactions.Length} transactions");
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

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"{block.Hash} ({block.Number}) adding result is {result}");
                }
            }
            else if (block.Number > BlockTree.BestSuggested.Number + 1)
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Received a block {block.Hash} ({block.Number}) from {receivedFrom} - need to resync");
                }

                RunSync();
            }
            else
            {
                Debug.Fail("above should be covering everything");
            }
        }

        public void HintBlock(Keccak hash, BigInteger number, NodeId receivedFrom)
        {
            _peers.TryGetValue(receivedFrom, out PeerInfo peerInfo);
            string errorMessage = $"Received a block hint from an unknown peer {receivedFrom}";
            if (peerInfo == null)
            {
                _logger.Error(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }

            peerInfo.NumberAvailable = BigInteger.Max(number, peerInfo.NumberAvailable);
            // TODO: sync?
        }

        public void AddNewTransaction(Transaction transaction, NodeId receivedFrom)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Received a pending transaction {transaction.Hash} from {receivedFrom}");
            }

            _transactionStore.AddPending(transaction);

            // TODO: reputation
        }

        public async Task AddPeer(ISynchronizationPeer synchronizationPeer)
        {
            if (_peers.ContainsKey(synchronizationPeer.NodeId))
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Sync peer already in peers collection: {synchronizationPeer.NodeId}");
                }
                return;
            }

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Adding synchronization peer {synchronizationPeer.NodeId}");
            }

            var tokenSource = _initCancellationTokenSources[synchronizationPeer.NodeId] = new CancellationTokenSource();

            await InitPeerInfo(synchronizationPeer, tokenSource.Token).ContinueWith(t =>
            {
                _initCancellationTokenSources.TryRemove(synchronizationPeer.NodeId, out var _);

                if (t.IsFaulted)
                {
                    if (_logger.IsErrorEnabled)
                    {
                        _logger.Error($"{nameof(AddPeer)} failed.", t.Exception);
                    }
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsWarnEnabled)
                    {
                        _logger.Warn($"Init peer info cancelled: {synchronizationPeer.NodeId}");
                    }
                }
                else
                {
                    RunSync();
                }
            });
        }

        public void RemovePeer(ISynchronizationPeer synchronizationPeer)
        {
            if (!_peers.TryRemove(synchronizationPeer.NodeId, out var _))
            {
                return;
            }
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Removing synchronization peer {synchronizationPeer.NodeId}");
            }

            lock (_isSyncingLock)
            {
                if (_isSyncing && (_currentSyncingPeer?.Peer.NodeId.Equals(synchronizationPeer.NodeId) ?? false))
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Requesting peer cancel with: {synchronizationPeer.NodeId}");
                    }
                    _syncCancellationTokenSource?.Cancel();
                }
            }

            if (_initCancellationTokenSources.TryGetValue(synchronizationPeer.NodeId, out var tokenSource))
            {
                tokenSource.Cancel();
            }
        }

        public void Start()
        {
            //StartActivePeersTimer();
            //_syncCancellationTokenSource = new CancellationTokenSource();
        }

        public async Task StopAsync()
        {
            //StopActivePeersTimer();

            if (_syncCancellationTokenSource == null || _syncCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            _syncCancellationTokenSource?.Cancel();
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

        //private void StartActivePeersTimer()
        //{
        //    if (_logger.IsInfoEnabled)
        //    {
        //        _logger.Info("Starting sync timer");
        //    }

        //    _syncTimer = new System.Timers.Timer(_blockchainConfig.SyncTimerInterval) { AutoReset = false };
        //    _syncTimer.Elapsed += (sender, e) =>
        //    {
        //        _syncTimer.Enabled = false;
        //        if (_logger.IsInfoEnabled)
        //        {
        //            _logger.Info($"Running sync timer: current sync peers: {_peers.Count}, unsynced: {_peers.Count(x => !x.Value.IsSynced)}, synced: {_peers.Count(x => x.Value.IsSynced)}");
        //        }
        //        RunSync();
        //        _syncTimer.Enabled = true;
        //    };

        //    _syncTimer.Start();
        //}

        //private void StopActivePeersTimer()
        //{
        //    try
        //    {
        //        if (_logger.IsInfoEnabled)
        //        {
        //            _logger.Info("Stopping sync timer");
        //        }
        //        _syncTimer?.Stop();
        //    }
        //    catch (Exception e)
        //    {
        //        _logger.Error("Error during sync timer stop", e);
        //    }
        //}

        private void OnNewHeadBlock(object sender, BlockEventArgs blockEventArgs)
        {
            if (_isSyncing)
            {
                return;
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
            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info("Sync in process");
                    }
                    return;
                }

                _isSyncing = true;

                if (_logger.IsInfoEnabled)
                {
                    _logger.Info("Starting aggregate sync");
                }
            }

            var syncTask = Task.Run(SyncAsync);
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
                    if (_logger.IsErrorEnabled)
                    {
                        _logger.Error("Aggregate sync process finished");
                    }
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info("Aggregate sync process finished");
                    }
                }
            });
        }

        private async Task SyncAsync()
        {
            while (true)
            {
                // TODO: order by total difficulty
                var peers = _peers.Where(x => !x.Value.IsSynced).OrderBy(p => p.Value.NumberAvailable).ToArray();

                if (!peers.Any())
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"No sync peers availible, finishing sync process, our block #: {BlockTree.BestSuggested.Number}");
                    }
                    return;
                }

                var peerInfo = peers.Last().Value;

                if (BlockTree.BestSuggested.Number >= peerInfo.NumberAvailable)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"No more peers with higher block number availible, finishing sync process, our highest block #: {BlockTree.BestSuggested.Number}, highest peer block #: {peerInfo.NumberAvailable}");
                    }
                    return;
                }

                _currentSyncingPeer = peerInfo;
                if (_logger.IsInfoEnabled)
                {
                    _logger.Info($"Starting sync processes with Node: {peerInfo.Peer.NodeId} [{peerInfo.Peer.ClientId}], peer highest block #: {peerInfo.NumberAvailable}, our highest block #: {BlockTree.BestSuggested.Number}");
                }

                _syncCancellationTokenSource = new CancellationTokenSource();

                var currentPeerNodeId = peerInfo?.Peer?.NodeId;

                var syncTask = PeerSyncAsync(_syncCancellationTokenSource.Token, peerInfo);
                await syncTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {     
                        if (_logger.IsErrorEnabled)
                        {
                            if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x is TimeoutException))
                            {
                                _logger.Warn($"Stopping Sync with node: {currentPeerNodeId}. {t.Exception.Message}");
                            }
                            else
                            {
                                _logger.Error($"Stopping Sync with node: {currentPeerNodeId}. Error in the sync process: {t.Exception?.Message}");
                            }
                        }
                        else if (_logger.IsDebugEnabled)
                        {
                            _logger.Error($"Stopping Sync with node: {currentPeerNodeId}. Error in the sync process", t.Exception);
                        }

                        //TODO check if we should disconnect this node
                        RemovePeer(peerInfo?.Peer);
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Sync with Node: {currentPeerNodeId} failed. Removed node from sync peers.");
                        }
                    }
                    else if (t.IsCanceled)
                    {
                        RemovePeer(peerInfo?.Peer);
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Sync with Node: {currentPeerNodeId} canceled. Removed node from sync peers.");
                        }
                    }
                    else if (t.IsCompleted)
                    {
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info($"Sync process finished with nodeId: {currentPeerNodeId}. Best block now is {BlockTree.BestSuggested.Hash} ({BlockTree.BestSuggested.Number})");
                        }
                    }

                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Finished sync process [{(t.IsFaulted ? "FAULTED" : t.IsCanceled ? "CANCELLED" : t.IsCompleted ? "COMPLETED" : "OTHER" )}] with Node: {peerInfo.Peer.NodeId} [{peerInfo.Peer.ClientId}], peer highest block #: {peerInfo.NumberAvailable}, our highest block #: {BlockTree.BestSuggested.Number}");
                    }
                });
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
                if (ancestorLookupLevel > maxLookup)
                {
                    throw new InvalidOperationException("Cannot find ancestor"); // TODO: remodel this after full sync test is added
                }

                if (token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                }

                if (!isCommonAncestorKnown)
                {
                    // TODO: cases when many peers used for sync and one peer finished sync and then we need resync - we should start from common point and not NumberReceived that may be far in the past
                    _logger.Info($"Finding common ancestor for {peerInfo.Peer.NodeId}");
                    isCommonAncestorKnown = true;
                }

                BigInteger blocksLeft = peerInfo.NumberAvailable - peerInfo.NumberReceived;
                // TODO: fault handling on tasks

                int blocksToRequest = (int)BigInteger.Min(blocksLeft + 1, BatchSize);
                if (_logger.IsDebugEnabled) _logger.Debug($"Sync request to peer with {peerInfo.NumberAvailable} blocks. Got {peerInfo.NumberReceived} and asking for {blocksToRequest} more.");
                Task<BlockHeader[]> headersTask = peer.GetBlockHeaders(peerInfo.NumberReceived, blocksToRequest, 0, token);
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
                    if(_logger.IsInfoEnabled) _logger.Info($"Received {blocks[i]} from {peer.NodeId}");
                    
                    if (_blockValidator.ValidateSuggestedBlock(blocks[i]))
                    {
                        AddBlockResult addResult = BlockTree.SuggestBlock(blocks[i]);
                        if (addResult == AddBlockResult.UnknownParent)
                        {
                            if(_logger.IsInfoEnabled) _logger.Info($"Block {blocks[i].Number} ignored (unknown parent)");
                            if (i == 0)
                            {
                                if(_logger.IsWarnEnabled) _logger.Warn("Resyncing split");
                                peerInfo.NumberReceived -= 1;
                                var syncTask = Task.Run(() => PeerSyncAsync(_syncCancellationTokenSource.Token, peerInfo), _syncCancellationTokenSource.Token);
                                await syncTask;
                            }
                            else
                            {
                                const string message = "Peer sent an inconsistent batch of block headers";
                                _logger.Error(message);
                                throw new EthSynchronizationException(message);
                            }
                        }

                        if (_logger.IsDebugEnabled) _logger.Debug($"Block {blocks[i].Number} suggested for processing");
                    }
                    else
                    {
                        if (_logger.IsWarnEnabled) _logger.Warn($"Block {blocks[i].Number} skipped (validation failed)");
                    }
                }

                peerInfo.NumberReceived = blocks[blocks.Length - 1].Number;

                bestNumber = BlockTree.BestSuggested.Number;
            }

            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Stopping sync processes with Node: {peerInfo.Peer.NodeId}, wasCancelled: {wasCancelled}");
            }

            if (!wasCancelled)
            {
                peerInfo.IsSynced = true;
                Synced?.Invoke(this, new SyncedEventArgs(peerInfo.Peer));
            }
        }

        public event EventHandler<SyncedEventArgs> Synced;

        private async Task InitPeerInfo(ISynchronizationPeer peer, CancellationToken token)
        {
            Task<Keccak> getHashTask = peer.GetHeadBlockHash();

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Requesting head block info from {peer.NodeId}");
            }

            Task<BigInteger> getNumberTask = peer.GetHeadBlockNumber(token);
            await Task.WhenAll(getHashTask, getNumberTask).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Error($"{nameof(InitPeerInfo)} failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }, token);

            if (_logger.IsDebugEnabled)
            {
                _logger.Info($"Received head block info from {peer.NodeId} with head block numer {getNumberTask.Result}");
            }

            //            bool addResult = _peers.TryAdd(peer.NodeId, new PeerInfo(peer, getNumberTask.Result));
            bool addResult = _peers.TryAdd(peer.NodeId, new PeerInfo(peer, getNumberTask.Result) { NumberReceived = BlockTree.BestSuggested.Number }); // TODO: cheating now with assumign the consistency of the chains
            if (!addResult)
            {
                _logger.Error($"Adding {nameof(PeerInfo)} failed for {peer.NodeId}");
            }

#if DEBUG
            bool getValueResult = _peers.TryGetValue(peer.NodeId, out PeerInfo peerInfo);
            if (!getValueResult)
            {
                _logger.Error($"Try get value failed on {nameof(PeerInfo)} {peer.NodeId}");
                int i = 0;
                foreach (KeyValuePair<NodeId, PeerInfo> keyValuePair in _peers)
                {
                    _logger.Error($"{i++}: {keyValuePair.Key} {keyValuePair.Value}");
                }
            }

            if (peerInfo == null)
            {
                _logger.Error($"Newly added {nameof(PeerInfo)} for {peer.NodeId} is null");
            }
#endif
        }

        private class PeerInfo
        {
            public PeerInfo(ISynchronizationPeer peer, BigInteger bestRemoteBlockNumber)
            {
                Peer = peer;
                NumberAvailable = bestRemoteBlockNumber;
            }

            public ISynchronizationPeer Peer { get; }
            public BigInteger NumberAvailable { get; set; }
            public BigInteger NumberReceived { get; set; }
            public bool IsSynced { get; set; }

            public override string ToString()
            {
                return $"Peer {Peer.NodeId}, Available {NumberAvailable}, Received {NumberReceived}, Is Synced: {IsSynced}";
            }
        }
    }
}