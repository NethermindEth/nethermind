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
using MathNet.Numerics;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Blockchain
{
    // TODO: forks
    public class SynchronizationManager : ISynchronizationManager
    {
        public const int BatchSize = 64;
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
                if (!(transaction.EthDeliveredBy?.Equals(nodeId) ?? false))
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
            throw new NotImplementedException();
        }

        public void AddNewBlock(Block block, PublicKey receivedFrom)
        {
            _peers.TryGetValue(receivedFrom, out PeerInfo peerInfo);
            Debug.Assert(peerInfo != null, $"Received notification from an unknown peer at {nameof(ISynchronizationManager)}");
            if (peerInfo == null)
            {
                throw new InvalidOperationException($"unknown synchronization peer {receivedFrom}");
            }

            peerInfo.NumberAvailable = block.Number;

            if (block.Number == HeadNumber + 1)
            {
                AddBlockResult result = BlockTree.AddBlock(block);
                // TODO: use for reputation later
                
                if (_logger.IsInfo)
                {
                    _logger.Info($"Received a {result} block {block.Hash} ({block.Number}) from the network with {block.Transactions.Length} transactions");
                }
            }
            else
            {
                if (_logger.IsInfo)
                {
                    _logger.Info($"Received a block {block.Hash} ({block.Number}) from the network - need to resync");
                }
                
                RunSync();
            }
        }

        public void HintBlock(Keccak hash, BigInteger number, PublicKey receivedFrom)
        {
            _peers.TryGetValue(receivedFrom, out PeerInfo peerInfo);
            Debug.Assert(peerInfo != null, $"Received notification from an unknown peer at {nameof(ISynchronizationManager)}");
            if (peerInfo == null)
            {
                throw new InvalidOperationException($"unknown synchronization peer {receivedFrom}");
            }

            peerInfo.NumberAvailable = number;

            {
                // TODO: if synced but received new block much higher than before then resync
            }
        }

        public void AddNewTransaction(Transaction transaction, PublicKey receivedFrom)
        {
            if (_logger.IsInfo)
            {
                _logger.Info($"Received a pending transaction {transaction.Hash} from the network");
            }
            
            _transactionStore.AddPending(transaction);

            // TODO: reputation
        }

        public async Task AddPeer(ISynchronizationPeer synchronizationPeer)
        {
            _logger.Info("SYNC MANAGER ADDING SYNCHRONIZATION PEER");
            await RefreshPeerInfo(synchronizationPeer);
            if (!_isSyncing)
            {
                RunSync();
            }
        }

        public void RemovePeer(ISynchronizationPeer synchronizationPeer)
        {
            throw new NotImplementedException();
        }

        public void Start()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            RunSync();
            // get a peer with a number higher than ours
            // sync
            // repeat

//            _currentSyncTask = Task.Factory.StartNew(async () =>
//                {
//                    while (!_cancellationTokenSource.IsCancellationRequested)
//                    {
//                        await RunRound(_cancellationTokenSource.Token);
//                    }
//                },
//                _cancellationTokenSource.Token);
        }

        public async Task StopAsync()
        {
            _cancellationTokenSource.Cancel();
            await _currentSyncTask;
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
                if (peerInfo.NumberAvailable < block.Number)
                {
                    peerInfo.Peer.SendNewBlock(block);
                }
            }
        }

        private void RunSync()
        {
            SyncAsync().ContinueWith(t =>
            {
                if (t.IsCompleted)
                {
                    if (_logger.IsInfo)
                    {
                        _logger.Info($"Sync process finished. Best block now is {BlockTree.BestSuggestedBlock.Hash} ({BlockTree.BestSuggestedBlock.Number})");
                    }
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsInfo)
                    {
                        _logger.Info($"Sync cancelled");
                    }
                }
                else if (t.IsFaulted)
                {
                    if (_logger.IsError)
                    {
                        _logger.Error($"Error in the sync process", t.Exception);
                    }
                }
            });
        }

        private async Task SyncAsync()
        {
            _isSyncing = true;
            bool wasCancelled = false;
            if (_peers.Any())
            {
                PeerInfo peerInfo = _peers.OrderBy(p => p.Value.NumberAvailable).Last().Value;
                ISynchronizationPeer peer = peerInfo.Peer;
                BigInteger bestNumber = BlockTree.BestSuggestedBlock.Number;
                while (peerInfo.NumberAvailable > bestNumber && peerInfo.NumberReceived <= bestNumber)
                {
                    BigInteger blocksLeft = peerInfo.NumberAvailable - bestNumber;
                    // TODO: fault handling on tasks

                    Task<BlockHeader[]> headersTask = peer.GetBlockHeaders(peerInfo.LastSyncedHash ?? GenesisBlock.Hash, (int)(BigInteger.Min(blocksLeft, BatchSize) + (bestNumber.IsZero ? 1 : 0)), bestNumber.IsZero ? 0 : 1);
                    _currentSyncTask = headersTask;
                    BlockHeader[] headers = await headersTask;
                    if (_currentSyncTask.IsCanceled)
                    {
                        wasCancelled = true;
                        break;
                    }

                    List<Keccak> hashes = new List<Keccak>();
                    Dictionary<Keccak, BlockHeader> headersByHash = new Dictionary<Keccak, BlockHeader>();
                    for (int i = 0; i < headers.Length; i++)
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

                    for (int i = 1; i < blocks.Length; i++)
                    {
                        blocks[i].Header = headersByHash[hashes[i]];
                        if (_blockValidator.ValidateSuggestedBlock(blocks[i]))
                        {
                            AddBlockResult addResult = BlockTree.AddBlock(blocks[i]);
                            peerInfo.NumberReceived = blocks[i].Number;
                            if (addResult == AddBlockResult.UnknownParent)
                            {
                                _logger.Debug($"BLOCK {blocks[i].Number} WAS IGNORED");
                                break;
                            }

                            _logger.Debug($"BLOCK {blocks[i].Number} WAS ADDED TO THE CHAIN");
                        }
                    }

                    bestNumber = BlockTree.BestSuggestedBlock.Number;
                }

                if (!wasCancelled)
                {
                    peerInfo.IsSynced = true;
                    Synced?.Invoke(this, EventArgs.Empty);
                }
            }

            _isSyncing = false;
        }

        public event EventHandler Synced;

        private async Task RefreshPeerInfo(ISynchronizationPeer peer)
        {
            Task<Keccak> getHashTask = peer.GetHeadBlockHash();
            _logger.Info("SYNC MANAGER - GETTING HEAD BLOCK INFO");
            Task<BigInteger> getNumberTask = peer.GetHeadBlockNumber();
            await Task.WhenAll(getHashTask, getNumberTask);
            _logger.Info("SYNC MANAGER - RECEIVED HEAD BLOCK INFO");
            _peers.AddOrUpdate(
                peer.NodeId,
                new PeerInfo(peer, getNumberTask.Result),
                (p, pi) =>
                {
                    if (pi == null)
                    {
                        Debug.Fail("unexpected null peer info");
                    }
                    else
                    {
                        pi.NumberAvailable = getNumberTask.Result;
                    }

                    return pi;
                });
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
            public Keccak LastSyncedHash { get; set; }
            public bool IsSynced { get; set; }
        }
    }
}