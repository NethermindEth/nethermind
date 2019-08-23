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
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Stats.Model;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public class SyncServer : ISyncServer
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly IEthSyncPeerPool _pool;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISealValidator _sealValidator;
        private readonly ISnapshotableDb _stateDb;
        private readonly ISnapshotableDb _codeDb;
        private readonly ISynchronizer _synchronizer;
        private readonly ISyncConfig _syncConfig;
        private object _dummyValue = new object();
        private LruCache<Keccak, object> _recentlySuggested = new LruCache<Keccak, object>(8);

        public SyncServer(ISnapshotableDb stateDb, ISnapshotableDb codeDb, IBlockTree blockTree, IReceiptStorage receiptStorage, ISealValidator sealValidator, IEthSyncPeerPool pool, ISynchronizer synchronizer, ISyncConfig syncConfig, ILogManager logManager)
        {
            _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _blockTree.NewHeadBlock += OnNewHeadBlock;
        }

        public int ChainId => _blockTree.ChainId;
        public BlockHeader Genesis => _blockTree.Genesis;

        public BlockHeader Head
        {
            get
            {
                if (_blockTree.Head == null)
                {
                    return null;
                }

                bool headIsGenesis = _blockTree.Head.Hash == _blockTree.Genesis.Hash;
                return headIsGenesis
                    ? _blockTree.FindHeader(new Keccak(_syncConfig.PivotHash ?? Keccak.Zero.ToString()), BlockTreeLookupOptions.None) ?? _blockTree.Genesis
                    : _blockTree.Head;
            }
        }

        public int GetPeerCount()
        {
            return _pool.PeerCount;
        }

        public void AddNewBlock(Block block, Node nodeWhoSentTheBlock)
        {
            if (block.TotalDifficulty == null) throw new InvalidOperationException("Cannot add a block with unknown total difficulty");

            _pool.TryFind(nodeWhoSentTheBlock.Id, out PeerInfo peerInfo);
            if (peerInfo == null)
            {
                string errorMessage = $"Received a new block from an unknown peer {nodeWhoSentTheBlock:c} {nodeWhoSentTheBlock.Id} {_pool.PeerCount}";
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

            if ((block.TotalDifficulty ?? 0) < _blockTree.BestSuggestedHeader.TotalDifficulty) return;

            lock (_recentlySuggested)
            {
                if (_recentlySuggested.Get(block.Hash) != null) return;
                _recentlySuggested.Set(block.Hash, _dummyValue);
            }

            if (block.Number > _blockTree.BestKnownNumber + 8)
            {
                // ignore blocks when syncing in a simple non-locking way
                _synchronizer.RequestSynchronization(SyncTriggerType.NewDistantBlock);
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Adding new block {block.ToString(Block.Format.Short)}) from {nodeWhoSentTheBlock:c}");

            if (!_sealValidator.ValidateSeal(block.Header)) throw new EthSynchronizationException("Peer sent a block with an invalid seal");

            if (block.Number <= _blockTree.BestKnownNumber + 1)
            {
                if (_logger.IsInfo)
                {
                    string authorString = block.Author == null ? string.Empty : "sealed by " + (KnownAddresses.GoerliValidators.ContainsKey(block.Author) ? KnownAddresses.GoerliValidators[block.Author] : block.Author?.ToString());
                    if (_logger.IsInfo) _logger.Info($"Discovered a new block {string.Empty.PadLeft(9 - block.Number.ToString().Length, ' ')}{block.ToString(Block.Format.HashNumberAndTx)} {authorString}, sent by {nodeWhoSentTheBlock:s}");
                }

                if (_logger.IsTrace) _logger.Trace($"{block}");

                if (_synchronizer.SyncMode == SyncMode.Full)
                {
                    AddBlockResult result = _blockTree.SuggestBlock(block);
                    if (_logger.IsTrace) _logger.Trace($"{block.Hash} ({block.Number}) adding result is {result}");
                    if (result == AddBlockResult.UnknownParent) _synchronizer.RequestSynchronization(SyncTriggerType.Reorganization);
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Received a block {block.Hash} ({block.Number}) from {nodeWhoSentTheBlock} - need to resync");
                _synchronizer.RequestSynchronization(SyncTriggerType.NewNearBlock);
            }
        }

        public void HintBlock(Keccak hash, long number, Node node)
        {
            if (!_pool.TryFind(node.Id, out PeerInfo peerInfo))
            {
                if (_logger.IsDebug) _logger.Debug($"Received a block hint from an unknown {node:c}, ignoring");
                return;
            }

            if (number > _blockTree.BestKnownNumber + 8) return;

            if (number > peerInfo.HeadNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"HINT Updating header of {peerInfo} from {peerInfo.HeadNumber} {peerInfo.TotalDifficulty} to {number}");
                peerInfo.HeadNumber = number;
                peerInfo.HeadHash = hash;

                lock (_recentlySuggested)
                {
                    if (_recentlySuggested.Get(hash) != null) return;

                    /* do not add as this is a hint only */
                }

                _pool.Refresh(node.Id);
            }
        }

        public TxReceipt[][] GetReceipts(Keccak[] blockHashes)
        {
            var receipts = new TxReceipt[blockHashes.Length][];
            for (int blockIndex = 0; blockIndex < blockHashes.Length; blockIndex++)
            {
                Block block = Find(blockHashes[blockIndex]);
                var blockReceipts = new TxReceipt[block?.Transactions.Length ?? 0];
                bool setNullForBlock = false;
                for (int receiptIndex = 0; receiptIndex < (block?.Transactions.Length ?? 0); receiptIndex++)
                {
                    if (block == null) continue;

                    TxReceipt receipt = _receiptStorage.Find(block.Transactions[receiptIndex].Hash);
                    if (receipt == null)
                    {
                        setNullForBlock = true;
                        break;
                    }
                    
                    receipt.BlockNumber = block.Number;
                    blockReceipts[receiptIndex] = receipt;
                }

                receipts[blockIndex] = setNullForBlock ? null : blockReceipts;
            }

            return receipts;
        }

        public BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse)
        {
            return _blockTree.FindHeaders(hash, numberOfBlocks, skip, reverse);
        }

        public byte[][] GetNodeData(Keccak[] keys)
        {
            var values = new byte[keys.Length][];
            for (int i = 0; i < keys.Length; i++)
            {
                values[i] = _stateDb.Get(keys[i]) ?? _codeDb.Get(keys[i]);
            }

            return values;
        }

        public Block Find(Keccak hash)
        {
            return _blockTree.FindBlock(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        public Keccak FindHash(long number)
        {
            return _blockTree.FindHash(number);
        }

        [Todo(Improve.Refactor, "This may not be desired if the other node is just syncing now too")]
        private void OnNewHeadBlock(object sender, BlockEventArgs blockEventArgs)
        {
            Block block = blockEventArgs.Block;
            if (_blockTree.BestKnownNumber > block.Number) return;

            int counter = 0;
            foreach (PeerInfo peerInfo in _pool.AllPeers)
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
    }
}