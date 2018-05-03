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
using Nethermind.Core.Extensions;

namespace Nethermind.Blockchain
{
    // TODO: forks
    public class SynchronizationManager : ISynchronizationManager
    {
        public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(10);

        public const int BatchSize = 16;
        private readonly IBlockValidator _blockValidator;
        private readonly IHeaderValidator _headerValidator;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<PublicKey, PeerInfo> _peers = new ConcurrentDictionary<PublicKey, PeerInfo>();
        private readonly ITransactionStore _transactionStore;
        private readonly ITransactionValidator _transactionValidator;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _currentSyncTask;
        private bool _isSyncing;

        public SynchronizationManager(
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            IHeaderValidator headerValidator,
            ITransactionStore transactionStore,
            ITransactionValidator transactionValidator,
            ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _transactionValidator = transactionValidator ?? throw new ArgumentNullException(nameof(transactionValidator));

            BlockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
            BlockTree.NewHeadBlock += OnNewHeadBlock;
            _transactionStore.NewPending += OnNewPendingTransaction;

            _logger.Info($"Initialized {nameof(SynchronizationManager)} with genesis block {HeadBlock}");
        }

        private void OnNewPendingTransaction(object sender, TransactionEventArgs transactionEventArgs)
        {
            Transaction transaction = transactionEventArgs.Transaction;
            foreach ((PublicKey nodeId, PeerInfo peerInfo) in _peers)
            {
                if (!(transaction.DeliveredBy?.Equals(nodeId) ?? false))
                {
                    peerInfo.Peer.SendNewTransaction(transaction);
                }
            }
        }

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

        public void AddNewBlock(Block block, PublicKey receivedFrom)
        {
            lock (_isSyncingLock)
            {
                if (_isSyncing)
                {
                    _logger.Debug($"Ignoring new block {block.Hash} while syncing");
                    return;
                }
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
                _logger.Debug($"Using {peerInfo}");
            }

            if (peerInfo == null)
            {
                string errorMessage = $"unknown synchronization peer {receivedFrom}";
                _logger.Error(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }

            peerInfo.NumberAvailable = BigInteger.Max(block.Number, peerInfo.NumberAvailable);

            if (block.Number <= BlockTree.BestSuggestedBlock.Number + 1)
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
            else if (block.Number > BlockTree.BestSuggestedBlock.Number + 1)
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

        public void HintBlock(Keccak hash, BigInteger number, PublicKey receivedFrom)
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

        public void AddNewTransaction(Transaction transaction, PublicKey receivedFrom)
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
            if (_logger.IsInfoEnabled)
            {
                _logger.Info($"Adding synchronization peer {synchronizationPeer.NodeId}");
            }

            await InitPeerInfo(synchronizationPeer).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(AddPeer)} failed.", t.Exception);
                }
            });

            RunSync();
        }

        public void RemovePeer(ISynchronizationPeer synchronizationPeer)
        {
            throw new NotImplementedException();
        }

        public void Start()
        {
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task StopAsync()
        {
            _cancellationTokenSource.Cancel();
            await (_currentSyncTask ?? Task.CompletedTask).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(StopAsync)} failed.", t.Exception);
                }
            });
        }

        public int ChainId => BlockTree.ChainId;
        public Block GenesisBlock => BlockTree.GenesisBlock;
        public Block HeadBlock => BlockTree.HeadBlock;
        public BigInteger HeadNumber => BlockTree.HeadBlock.Number;
        public BigInteger TotalDifficulty => BlockTree.HeadBlock?.TotalDifficulty ?? 0;
        public IBlockTree BlockTree { get; set; }

        private void OnNewHeadBlock(object sender, BlockEventArgs blockEventArgs)
        {
            Block block = blockEventArgs.Block;
            foreach ((PublicKey nodeId, PeerInfo peerInfo) in _peers)
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
                    return;
                }

                _isSyncing = true;
            }

            SyncAsync().ContinueWith(t =>
            {
                lock (_isSyncingLock)
                {
                    _isSyncing = false;
                }

                if (t.IsFaulted)
                {
                    if (_logger.IsErrorEnabled)
                    {
                        _logger.Error($"Error in the sync process", t.Exception);
                        throw t.Exception;
                    }
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Sync process finished. Best block now is {BlockTree.BestSuggestedBlock.Hash} ({BlockTree.BestSuggestedBlock.Number})");
                    }
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Sync cancelled");
                    }
                }
            });
        }

        private async Task SyncAsync()
        {
            bool wasCancelled = false;
            if (_peers.Any())
            {
                PeerInfo peerInfo = _peers.OrderBy(p => p.Value.NumberAvailable).Last().Value; // TODO: order by total difficulty
                ISynchronizationPeer peer = peerInfo.Peer;
                BigInteger bestNumber = BlockTree.BestSuggestedBlock.Number;

                bool isCommonAncestorKnown = false;

                while (peerInfo.NumberAvailable > bestNumber && peerInfo.NumberReceived <= peerInfo.NumberAvailable)
                {
                    if (!isCommonAncestorKnown)
                    {
                        // TODO: cases when many peers used for sync and one peer finished sync and then we need resync - we should start from common point and not NumberReceived that may be far in the past
                        _logger.Info($"Finding common ancestor for {peerInfo.Peer.NodeId}");
                        isCommonAncestorKnown = true;
                    }

                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"Sending sync request to peer with best {peerInfo.NumberAvailable} (I received {peerInfo.NumberReceived}), is synced {peerInfo.IsSynced}");
                    }

                    BigInteger blocksLeft = peerInfo.NumberAvailable - peerInfo.NumberReceived;
                    // TODO: fault handling on tasks

                    Task<BlockHeader[]> headersTask = peer.GetBlockHeaders(peerInfo.NumberReceived, (int)BigInteger.Min(blocksLeft + 1, BatchSize), 0);
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

                    List<Keccak> hashes = new List<Keccak>();
                    Dictionary<Keccak, BlockHeader> headersByHash = new Dictionary<Keccak, BlockHeader>();
                    for (int i = 1; i < headers.Length; i++)
                    {
                        hashes.Add(headers[i].Hash);
                        headersByHash[headers[i].Hash] = headers[i];
                    }

                    Task<Block[]> bodiesTask = peer.GetBlocks(hashes.ToArray());
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

                    for (int i = 0; i < blocks.Length; i++)
                    {
                        blocks[i].Header = headersByHash[hashes[i]];

                        if (_logger.IsTraceEnabled)
                        {
                            _logger.Trace("RECEIVED BLOCK:");
                            _logger.Trace($"{blocks[i]}");
                        }

                        if (_blockValidator.ValidateSuggestedBlock(blocks[i]))
                        {
                            AddBlockResult addResult = BlockTree.SuggestBlock(blocks[i]);
                            if (addResult == AddBlockResult.UnknownParent)
                            {
                                _logger.Info($"Block {blocks[i].Number} ignored (unknown parent)");
                                if (i == 0)
                                {
                                    _logger.Warn("Resyncing split");
                                    peerInfo.NumberReceived -= 1;
                                    await SyncAsync();
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

                    bestNumber = BlockTree.BestSuggestedBlock.Number;
                }

                if (!wasCancelled)
                {
                    peerInfo.IsSynced = true;
                    Synced?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler Synced;

        private async Task InitPeerInfo(ISynchronizationPeer peer)
        {
            Task<Keccak> getHashTask = peer.GetHeadBlockHash();

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Requesting head block info from {peer.NodeId}");
            }

            Task<BigInteger> getNumberTask = peer.GetHeadBlockNumber();
            await Task.WhenAll(getHashTask, getNumberTask).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Error($"{nameof(InitPeerInfo)} failed.", t.Exception);
                    }
                });

            if (_logger.IsDebugEnabled)
            {
                _logger.Info($"Received head block info from {peer.NodeId} with head block numer {getNumberTask.Result}");
            }

//            bool addResult = _peers.TryAdd(peer.NodeId, new PeerInfo(peer, getNumberTask.Result));
            bool addResult = _peers.TryAdd(peer.NodeId, new PeerInfo(peer, getNumberTask.Result) {NumberReceived = this.HeadNumber}); // TODO: cheating now with assumign the consistency of the chains
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
                foreach (KeyValuePair<PublicKey, PeerInfo> keyValuePair in _peers)
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