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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining;
using Nethermind.Stats.Model;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public class SynchronizationServer : ISynchronizationServer
    {
        private readonly ISynchronizer _synchronizer;
        private readonly IEthSyncPeerSelector _selector;
        private readonly ISealValidator _sealValidator;
        private readonly ISnapshotableDb _stateDb;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ILogger _logger;
        private LruCache<Keccak, object> _recentlySuggested = new LruCache<Keccak, object>(8);
        private object _dummyValue = new object();

        public int ChainId => _blockTree.ChainId;
        public BlockHeader Genesis => _blockTree.Genesis;
        public BlockHeader Head => _blockTree.Head;

        public int GetPeerCount()
        {
            return _selector.PeerCount;
        }
        
        public SynchronizationServer(ISynchronizer synchronizer, IEthSyncPeerSelector selector, ISealValidator sealValidator, ISnapshotableDb stateDb, IBlockTree blockTree, IReceiptStorage receiptStorage, ILogManager logManager)
        {
            _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
            _selector = selector ?? throw new ArgumentNullException(nameof(selector));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public Task StopAsync()
        {
            return Task.CompletedTask;
        }
        
        [Todo(Improve.Refactor, "This may not be desired if the other node is just syncing now too")]
        private void OnNewHeadBlock(object sender, BlockEventArgs blockEventArgs)
        {
            Block block = blockEventArgs.Block;
            if (_blockTree.BestKnownNumber > block.Number)
            {
                return;
            }

            int counter = 0;
            foreach (PeerInfo peerInfo in _selector.AllPeers)
            {
                if (peerInfo.TotalDifficulty < (block.TotalDifficulty ?? UInt256.Zero))
                {
                    peerInfo.SyncPeer.SendNewBlock(block);
                    counter++;
                }
            }

            if (counter > 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Broadcasting block {block.ToString(Block.Format.Short)} to {counter} peers.");
            }
        }
        
        public void AddNewBlock(Block block, Node nodeWhoSentTheBlock)
        {
            if (block.TotalDifficulty == null)
            {
                throw new InvalidOperationException("Cannot add a block with unknown total difficulty");
            }

            _selector.TryFind(nodeWhoSentTheBlock.Id, out PeerInfo peerInfo);
            if (peerInfo == null)
            {
                string errorMessage = $"Received a new block from an unknown peer {nodeWhoSentTheBlock:c} {nodeWhoSentTheBlock.Id} {_peers.Count}";
                if (_logger.IsDebug) _logger.Debug(errorMessage);
                return;
            }

            if ((block.TotalDifficulty ?? 0) > peerInfo.TotalDifficulty)
            {
                if (_logger.IsTrace) _logger.Trace($"ADD NEW BLOCK Updating header of {peerInfo} from {peerInfo.HeadNumber} {peerInfo.TotalDifficulty} to {block.Number} {block.TotalDifficulty}");
                peerInfo.HeadNumber = block.Number;
                peerInfo.HeadHash = block.Hash;
                peerInfo.TotalDifficulty = block.TotalDifficulty ?? peerInfo.TotalDifficulty;
            }

            if ((block.TotalDifficulty ?? 0) < _blockTree.BestSuggested.TotalDifficulty)
            {
                return;
            }

            if (block.Number > _blockTree.BestKnownNumber + 8)
            {
                // ignore blocks when syncing in a simple non-locking way
                _synchronizer.RequestSynchronization();
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

            if (_logger.IsTrace) _logger.Trace($"Adding new block {block.Hash} ({block.Number}) from {nodeWhoSentTheBlock:c}");

            if (!_sealValidator.ValidateSeal(block.Header))
            {
                throw new EthSynchronizationException("Peer sent a block with an invalid seal");
            }

            if (block.Number <= _blockTree.BestKnownNumber + 1)
            {
                if (_logger.IsInfo)
                {
                    string authorString = block.Author == null ? string.Empty : "by " + (KnownAddresses.GoerliValidators.ContainsKey(block.Author) ? KnownAddresses.GoerliValidators[block.Author] : block.Author?.ToString());
                    if (_logger.IsInfo) _logger.Info($"Discovered a new block {block.ToString(Block.Format.HashNumberAndTx)} {authorString} from {nodeWhoSentTheBlock:s}");
                }

                if (_logger.IsTrace) _logger.Trace($"{block}");

                AddBlockResult result = _blockTree.SuggestBlock(block);
                if (_logger.IsTrace) _logger.Trace($"{block.Hash} ({block.Number}) adding result is {result}");
                if (result == AddBlockResult.UnknownParent)
                {
                    /* here we want to cover scenario when our peer is reorganizing and sends us a head block
                     * from a new branch and we need to sync previous blocks as we do not know this block's parent */
                    _synchronizer.RequestSynchronization();
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Received a block {block.Hash} ({block.Number}) from {nodeWhoSentTheBlock} - need to resync");
                _synchronizer.RequestSynchronization();
            }
        }

        public void HintBlock(Keccak hash, UInt256 number, Node node)
        {
            if (!_selector.TryFind(node.Id, out PeerInfo peerInfo))
            {
                if (_logger.IsDebug) _logger.Debug($"Received a block hint from an unknown {node:c}, ignoring");
                return;
            }

            if (number > _blockTree.BestKnownNumber + 8)
            {
                // ignore blocks when syncing in a simple non-locking way
                return;
            }

            if (number > peerInfo.HeadNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"HINT Updating header of {peerInfo} from {peerInfo.HeadNumber} {peerInfo.TotalDifficulty} to {number}");
                peerInfo.HeadNumber = number;
                peerInfo.HeadHash = hash;

                lock (_recentlySuggested)
                {
                    if (_recentlySuggested.Get(hash) != null)
                    {
                        return;
                    }

                    /* do not add as this is a hint only */
                }

                _selector.Refresh(peerInfo);
            }
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
        
        public byte[][] GetNodeData(Keccak[] keys)
        {
            byte[][] values = new byte[keys.Length][];
            for (int i = 0; i < keys.Length; i++)
            {
                values[i] = _stateDb.Get(keys[i]);
            }

            return values;
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
    }
}