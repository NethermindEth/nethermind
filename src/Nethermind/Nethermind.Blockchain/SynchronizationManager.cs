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
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain
{
    public class SynchronizationManager : ISynchronizationManager
    {
        private readonly TimeSpan _delay;
        private readonly IBlockValidator _blockValidator;
        private readonly IHeaderValidator _headerValidator;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<ISynchronizationPeer, PeerInfo> _peers = new ConcurrentDictionary<ISynchronizationPeer, PeerInfo>();
        private readonly ISpecProvider _specProvider;
        private readonly Dictionary<Keccak, BlockInfo> _storedBlocks = new Dictionary<Keccak, BlockInfo>();

        private readonly object _syncObject = new object();

        private readonly Dictionary<Keccak, TransactionInfo> _transactions = new Dictionary<Keccak, TransactionInfo>();
        private readonly ITransactionValidator _transactionValidator;

        private CancellationTokenSource _cancellationTokenSource;

        private object _lockObject = new object();

        private int _round; // TODO: remove when no longer needed for diagnostics

        private Task _syncTask;

        public SynchronizationManager(
            TimeSpan delay,
            IBlockTree blockTree,
            IHeaderValidator headerValidator,
            IBlockValidator blockValidator,
            ITransactionValidator transactionValidator,
            ISpecProvider specProvider,
            Block genesisBlock,
            Block bestBlockSoFar,
            BigInteger totalDifficulty,
            ILogger logger)
        {
            _delay = delay;
            BlockTree = blockTree;
            BlockTree.BlockAddedToMain += OnBlockAddedToMain;

            _headerValidator = headerValidator;
            _blockValidator = blockValidator;
            _transactionValidator = transactionValidator;
            _specProvider = specProvider;
            _logger = logger;
            BlockInfo blockInfo = AddBlock(bestBlockSoFar, new PublicKey(new byte[64]));
            if (blockInfo.BlockQuality == Quality.Invalid)
            {
                throw new EthSynchronizationException("Provided genesis block is not valid");
            }

            BestBlock = bestBlockSoFar.Hash;
            GenesisBlock = genesisBlock.Hash;
            BestNumber = bestBlockSoFar.Number;
            TotalDifficulty = totalDifficulty;
            _logger.Info($"Initialized {nameof(SynchronizationManager)} with genesis block {bestBlockSoFar.Hash}");
        }

        public BlockInfo AddBlockHeader(BlockHeader blockHeader)
        {
            if (!_storedBlocks.ContainsKey(blockHeader.Hash))
            {
                HintBlock(blockHeader.Hash, blockHeader.Number);
            }

            _storedBlocks.TryGetValue(blockHeader.Hash, out BlockInfo blockInfo);
            if (blockInfo.HeaderQuality != Quality.Unknown)
            {
                return blockInfo;
            }

            bool isValid = _headerValidator.Validate(blockHeader);
            blockInfo.HeaderQuality = isValid ? Quality.DataValid : Quality.Invalid;
            blockInfo.BlockHeader = blockHeader;
            blockInfo.HeaderLocation = BlockDataLocation.Memory;

            return blockInfo;
        }

        public BlockInfo Find(Keccak hash)
        {
            if (_storedBlocks.ContainsKey(hash))
            {
                return _storedBlocks[hash];
            }

            return null;
        }

        public BlockInfo[] Find(Keccak hash, int numberOfBlocks, int skip, bool reverse)
        {
            Block[] blocks = BlockTree.FindBlocks(hash, numberOfBlocks, skip, reverse);
            BlockInfo[] blockInfos = new BlockInfo[blocks.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                if (blocks[i] == null)
                {
                    continue;
                }
                
                Debug.Assert(_storedBlocks.ContainsKey(blocks[i].Hash));
                blockInfos[i] = _storedBlocks[blocks[i].Hash];
            }

            return blockInfos;
        }

        public BlockInfo Find(BigInteger number)
        {
            throw new NotImplementedException();
        }

        public BlockInfo AddBlock(Block block, PublicKey receivedFrom)
        {
            if (!_storedBlocks.ContainsKey(block.Hash))
            {
                HintBlock(block.Hash, block.Number);
            }

            _storedBlocks.TryGetValue(block.Hash, out BlockInfo blockInfo);
            if (blockInfo.BlockQuality != Quality.Unknown)
            {
                return blockInfo;
            }

            bool isValid = _blockValidator.ValidateSuggestedBlock(block);
            blockInfo.HeaderQuality = isValid ? Quality.DataValid : Quality.Invalid;
            blockInfo.BlockHeader = block.Header;
            blockInfo.HeaderLocation = BlockDataLocation.Memory;
            blockInfo.Block = block;
            blockInfo.BodyLocation = BlockDataLocation.Memory;
            blockInfo.ReceivedFrom = receivedFrom;

            return blockInfo;
        }

        public void HintBlock(Keccak hash, BigInteger number)
        {
            if (!_storedBlocks.ContainsKey(hash))
            {
                _storedBlocks[hash] = new BlockInfo(hash, number);
            }
        }

        public TransactionInfo Add(Transaction transaction, PublicKey receivedFrom)
        {
            _transactions.TryGetValue(transaction.Hash, out TransactionInfo info);
            if (info == null)
            {
                info = new TransactionInfo(transaction, receivedFrom);
                info.Quality = _transactionValidator.IsWellFormed(transaction, _specProvider.CurrentSpec) ? Quality.DataValid : Quality.Invalid;
            }

            return info;
        }

        public void MarkAsProcessed(Transaction transaction, bool isValid)
        {
            _transactions.TryGetValue(transaction.Hash, out TransactionInfo info);
            if (info != null)
            {
                info.Quality = isValid ? Quality.Processed : Quality.Invalid;
            }
        }

        public void MarkAsProcessed(Block transaction, bool isValid)
        {
            _storedBlocks.TryGetValue(transaction.Hash, out BlockInfo info);
            if (info != null)
            {
                info.BlockQuality = isValid ? Quality.Processed : Quality.Invalid;
            }
        }

        public void AddPeer(ISynchronizationPeer synchronizationPeer)
        {
            _logger.Info("SYNC MANAGER ADDING SYNCHRONIZATION PEER");
            _peers.TryAdd(synchronizationPeer, null);
        }

        public void RemovePeer(ISynchronizationPeer synchronizationPeer)
        {
            throw new NotImplementedException();
        }

        public void Start()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _syncTask = Task.Factory.StartNew(async () =>
                {
                    while (!_cancellationTokenSource.IsCancellationRequested)
                    {
                        await RunRound(_cancellationTokenSource.Token);
                    }
                },
                _cancellationTokenSource.Token);
        }

        public async Task StopAsync()
        {
            _cancellationTokenSource.Cancel();
            await _syncTask;
        }

        public int ChainId => _specProvider.ChainId;
        public Keccak GenesisBlock { get; set; }
        public Keccak BestBlock { get; set; }
        public BigInteger BestNumber { get; set; }
        public BigInteger TotalDifficulty { get; set; }
        public IBlockTree BlockTree { get; set; }

        private void OnBlockAddedToMain(object sender, BlockEventArgs blockEventArgs)
        {
            lock (_syncObject)
            {
                Block block = blockEventArgs.Block;
                if (block.TotalDifficulty > TotalDifficulty)
                {
                    BestBlock = block.Hash;
                    BestNumber = block.Number;
                    TotalDifficulty = block.TotalDifficulty ?? 0;
                }

                if (!_storedBlocks.ContainsKey(block.Hash))
                {
                    AddBlock(block, null);
                }
            }
        }

        public Block Load(Keccak hash)
        {
            if (!_storedBlocks.ContainsKey(hash))
            {
                throw new InvalidOperationException("Trying to load an unknown block.");
            }

            BlockInfo blockInfo = _storedBlocks[hash];
            if (blockInfo.BodyLocation == BlockDataLocation.Remote || blockInfo.HeaderLocation == BlockDataLocation.Remote)
            {
                throw new InvalidOperationException("Cannot load block that has not been synced yet.");
            }

            if (blockInfo.BodyLocation == BlockDataLocation.Store || blockInfo.HeaderLocation == BlockDataLocation.Store)
            {
                throw new NotImplementedException("Block persistence not implemented yet");
            }

            return blockInfo.Block;
        }

        public void MarkProcessed(Keccak hash, bool isValid)
        {
            if (!_storedBlocks.ContainsKey(hash))
            {
                throw new InvalidOperationException("Trying to mark an unknown block as processed.");
            }

            BlockInfo blockInfo = _storedBlocks[hash];
            blockInfo.BlockQuality = isValid ? Quality.Processed : Quality.Invalid;
        }

        private async Task RefreshPeerInfo(ISynchronizationPeer peer)
        {
            Task getHashTask = peer.GetHeadBlockHash();
            _logger.Info($"SYNC MANAGER - GETTING HEAD BLOCK INFO");
            Task<BigInteger> getNumberTask = peer.GetHeadBlockNumber();
            await Task.WhenAll(getHashTask, getNumberTask);
            _logger.Info($"SYNC MANAGER - RECEIVED HEAD BLOCK INFO");
            _peers.AddOrUpdate(
                peer,
                new PeerInfo(peer, getNumberTask.Result),
                (p, pi) =>
                {
                    if (pi == null)
                    {
                        pi = new PeerInfo(p, getNumberTask.Result);
                    }
                    else
                    {
                        pi.Number = getNumberTask.Result;
                    }

                    return pi;
                });
        }

        private async Task RunRound(CancellationToken cancellationToken)
        {
            _logger.Info($"SYNC MANAGER ROUND {_round++}, {_peers.Count} " + (_peers.Count == 1 ? "PEER" : "PEERS"));
            List<Task> refreshTasks = new List<Task>();
            foreach (KeyValuePair<ISynchronizationPeer, PeerInfo> keyValuePair in _peers)
            {
                refreshTasks.Add(RefreshPeerInfo(keyValuePair.Key));
            }

            _logger.Info($"SYNC MANAGER WAITING FOR REFRESH");
            await Task.WhenAny(Task.WhenAll(refreshTasks), AsTask(cancellationToken));
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Info($"SYNC MANAGER CANCELLED");
                return;
            }

            _logger.Info($"SYNC MANAGER WILL GET MISSING BLOCKS NOW FROM {_peers.Count} PEERS");
            foreach ((ISynchronizationPeer peer, PeerInfo peerInfo) in _peers)
            {
                _logger.Info($"CALCULATING MISSING BLOCKS");
                _logger.Info($"PEER INFO = {peerInfo}, NUMBER = {peerInfo?.Number}, BEST = {BestNumber}");
                BigInteger missingBlocks = (peerInfo?.Number ?? 0) - BestNumber;
                _logger.Info($"SYNC MANAGER REQUESTING {missingBlocks} BLOCKS FROM PEER");
                if (missingBlocks > 0)
                {
                    Block[] blocks = await peer.GetBlocks(BestBlock, missingBlocks + 1);
                    foreach (Block block in blocks.Skip(1))
                    {
                        _logger.Info($"SYNC MANAGER RECEIVED BLOCK {block.Number}");
                        AddBlockResult addResult = BlockTree.AddBlock(block);
                        if (addResult == AddBlockResult.Ignored)
                        {
                            _logger.Info($"BLOCK {block.Number} WAS IGNORED");
                            break;
                        }
                        else
                        {
                            _logger.Info($"BLOCK {block.Number} WAS ADDED TO THE CHAIN");
                        }
                    }
                }
            }

            RoundFinished?.Invoke(this, EventArgs.Empty);
            
            _logger.Info($"SYNC MANAGER WAITING {_delay.TotalMilliseconds}ms");
            await Task.Delay(_delay, cancellationToken);
        }

        public EventHandler RoundFinished;
        
        // https://stackoverflow.com/questions/27238232/how-can-i-cancel-task-whenall/27238463
        private Task AsTask(CancellationToken token)
        {
            TaskCompletionSource<object> completionSource = new TaskCompletionSource<object>();
            token.Register(() => completionSource.TrySetCanceled(), false);
            return completionSource.Task;
        }

        private class PeerInfo
        {
            public PeerInfo(ISynchronizationPeer peer, BigInteger bestBlockNumber)
            {
                Peer = peer;
                Number = bestBlockNumber;
            }

            public ISynchronizationPeer Peer { get; set; }
            public BigInteger? Number { get; set; }
        }
    }
}