//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization
{
    /// <summary>
    /// This class is responsible for serving sync requests from other nodes and for broadcasting new data to peers.
    /// </summary>
    public class SyncServer : ISyncServer
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly ISyncPeerPool _pool;
        private readonly ISyncModeSelector _syncModeSelector;
        private readonly IReceiptFinder _receiptFinder;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISnapshotableDb _stateDb;
        private readonly ISnapshotableDb _codeDb;
        private readonly ISyncConfig _syncConfig;
        private object _dummyValue = new object();
        private ICache<Keccak, object> _recentlySuggested = new LruCacheWithRecycling<Keccak, object>(128, 128, "recently suggested blocks");
        private long _pivotNumber;

        public SyncServer(
            ISnapshotableDb stateDb,
            ISnapshotableDb codeDb,
            IBlockTree blockTree,
            IReceiptFinder receiptFinder,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncPeerPool pool,
            ISyncModeSelector syncModeSelector,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _pivotNumber = _syncConfig.PivotNumberParsed;

            _blockTree.NewHeadBlock += OnNewHeadBlock;
            _pivotHash = new Keccak(_syncConfig.PivotHash ?? Keccak.Zero.ToString());
        }

        private Keccak _pivotHash;

        private BlockHeader _pivotHeader;

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
                if (headIsGenesis)
                {
                    _pivotHeader ??= _blockTree.FindHeader(_pivotHash, BlockTreeLookupOptions.None);
                }

                return headIsGenesis ? _pivotHeader ?? _blockTree.Genesis : _blockTree.Head?.Header;
            }
        }

        public int GetPeerCount()
        {
            return _pool.PeerCount;
        }

        private Guid _sealValidatorUserGuid = Guid.NewGuid();

        public void AddNewBlock(Block block, ISyncPeer nodeWhoSentTheBlock)
        {
            if (block.TotalDifficulty == null)
            {
                throw new InvalidDataException("Cannot add a block with unknown total difficulty");
            }

            // Now, there are some complexities here.
            // We can have a scenario when a node sends us a block whose parent we do not know.
            // In such cases we cannot verify the total difficulty of the block
            // but we do want to update the information about what the peer believes its total difficulty is.
            // This is tricky because we may end up preferring the node over others just because it is convinced
            // to have higher total difficulty .
            // Also, note, Parity consistently sends invalid TotalDifficulty values both on Clique and Mainnet.
            // So even validating the total difficulty and disconnecting misbehaving nodes leads to problems
            // - it creates an impression of Nethermind being unstable with peers.
            // So, in the end, we decide to update the peer info, but soon after nullify the TotalDifficulty information
            // so that the block tree may actually calculate it when the parent is known.
            // (in case the parent is unknown the tree will ignore the block anyway).
            // The other risky scenario (as happened in the past) is when we nor validate the TotalDifficulty
            // neither nullify it and then BlockTree may end up saving a header or block with incorrect value set.
            // This may lead to corrupted block tree history.
            UpdatePeerInfoBasedOnBlockData(block, nodeWhoSentTheBlock);

            // Now it is important that all the following checks happen after the peer information was updated
            // even if the block is not something that we want to include in the block tree
            // it delivers information about the peer's chain.

            bool isBlockBeforeTheSyncPivot = block.Number < _pivotNumber;
            bool isBlockOlderThanMaxReorgAllows = block.Number < (_blockTree.Head?.Number ?? 0) - Sync.MaxReorgLength;
            bool isBlockTotalDifficultyLow = block.TotalDifficulty < _blockTree.BestSuggestedHeader.TotalDifficulty;
            if (isBlockBeforeTheSyncPivot || isBlockTotalDifficultyLow || isBlockOlderThanMaxReorgAllows) return;

            lock (_recentlySuggested)
            {
                if (_recentlySuggested.Get(block.Hash) != null) return;
                _recentlySuggested.Set(block.Hash, _dummyValue);
            }

            ValidateSeal(block, nodeWhoSentTheBlock);
            if ((_syncModeSelector.Current & (SyncMode.FastSync | SyncMode.StateNodes)) == SyncMode.None 
                || (_syncModeSelector.Current & (SyncMode.Full | SyncMode.Beam)) != SyncMode.None)
            {
                LogBlockAuthorNicely(block, nodeWhoSentTheBlock);
                SyncBlock(block, nodeWhoSentTheBlock);
            }
        }

        private void ValidateSeal(Block block, ISyncPeer syncPeer)
        {
            if (_logger.IsTrace) _logger.Trace($"Validating seal of {block.ToString(Block.Format.Short)}) from {syncPeer:c}");

            // We hint validation range mostly to help ethash to cache epochs.
            // It is important that we only do that here, after we ensured that the block is
            // in the range of [Head - MaxReorganizationLength, Head].
            // Otherwise we could hint incorrect ranges and cause expensive cache recalculations.
            _sealValidator.HintValidationRange(_sealValidatorUserGuid, block.Number - 128, block.Number + 1024);
            if (!_sealValidator.ValidateSeal(block.Header, true))
            {
                string message = $"Peer {syncPeer?.Node:c} sent a block with an invalid seal";
                if (_logger.IsDebug) _logger.Debug($"Peer {syncPeer?.Node:c} sent a block with an invalid seal");
                throw new EthSyncException(message);
            }
        }

        private void UpdatePeerInfoBasedOnBlockData(Block block, ISyncPeer syncPeer)
        {
            if ((block.TotalDifficulty ?? 0) > syncPeer.TotalDifficulty)
            {
                if (_logger.IsTrace) _logger.Trace($"ADD NEW BLOCK Updating header of {syncPeer} from {syncPeer.HeadNumber} {syncPeer.TotalDifficulty} to {block.Number} {block.TotalDifficulty}");
                syncPeer.HeadNumber = block.Number;
                syncPeer.HeadHash = block.Hash;
                syncPeer.TotalDifficulty = block.TotalDifficulty ?? syncPeer.TotalDifficulty;
            }
        }

        private void SyncBlock(Block block, ISyncPeer syncPeer)
        {
            if (_logger.IsTrace) _logger.Trace($"{block}");

            // we do not trust total difficulty from peers
            // Parity sends invalid data here and it is equally expensive to validate and to set from null
            block.Header.TotalDifficulty = null;

            bool isKnownParent = _blockTree.IsKnownBlock(block.Number - 1, block.ParentHash);
            if (isKnownParent)
            {
                if (!_blockValidator.ValidateSuggestedBlock(block))
                {
                    string message = $"Peer {syncPeer?.Node:c} sent an invalid block";
                    if (_logger.IsDebug) _logger.Debug(message);
                    lock (_recentlySuggested)
                    {
                        _recentlySuggested.Delete(block.Hash);
                    }

                    throw new EthSyncException(message);
                }

                AddBlockResult result = _blockTree.SuggestBlock(block, true);
                if (_logger.IsTrace) _logger.Trace($"{block.Hash} ({block.Number}) adding result is {result}");
            }

            // TODO: now it should be done by sync peer pool?
            // // do not change to if..else
            // // there are some rare cases when it did not work...
            // // do not remember why
            // if (result == AddBlockResult.UnknownParent)
            // {
            //     _synchronizer.RequestSynchronization(SyncTriggerType.NewBlock);
            // }
        }

        private void LogBlockAuthorNicely(Block block, ISyncPeer syncPeer)
        {
            // This line is not particularly important (just for logging)
            // and somehow it got refactored by ReSharper into some nightmare line.
            // It would be worth to split it back into something readable.
            // Generally it tries to find the sealer / miner name.
            string authorString = (block.Author == null ? null : "sealed by " + (KnownAddresses.GoerliValidators.ContainsKey(block.Author) ? KnownAddresses.GoerliValidators[block.Author] : block.Author?.ToString())) ?? (block.Beneficiary == null ? string.Empty : "mined by " + (KnownAddresses.KnownMiners.ContainsKey(block.Beneficiary) ? KnownAddresses.KnownMiners[block.Beneficiary] : block.Beneficiary?.ToString()));
            if (_logger.IsInfo)
            {
                if (_logger.IsInfo) _logger.Info($"Discovered a new block {string.Empty.PadLeft(9 - block.Number.ToString().Length, ' ')}{block.ToString(Block.Format.HashNumberAndTx)} {authorString}, sent by {syncPeer:s}");
            }
        }

        public void HintBlock(Keccak hash, long number, ISyncPeer syncPeer)
        {
            if (number > syncPeer.HeadNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"HINT Updating header of {syncPeer} from {syncPeer.HeadNumber} {syncPeer.TotalDifficulty} to {number}");
                syncPeer.HeadNumber = number;
                syncPeer.HeadHash = hash;

                lock (_recentlySuggested)
                {
                    if (_recentlySuggested.Get(hash) != null) return;

                    /* do not add as this is a hint only */
                }

                if (!_blockTree.IsKnownBlock(number, hash))
                {
                    _pool.RefreshTotalDifficulty(syncPeer, hash);
                    // TODO: now it should be done by sync peer pool?
                    // _synchronizer.RequestSynchronization(SyncTriggerType.NewBlock);
                }
            }
        }

        public TxReceipt[][] GetReceipts(IList<Keccak> blockHashes)
        {
            var receipts = new TxReceipt[blockHashes.Count][];
            for (int blockIndex = 0; blockIndex < blockHashes.Count; blockIndex++)
            {
                Keccak blockHash = blockHashes[blockIndex];
                receipts[blockIndex] = blockHash != null ? _receiptFinder.Get(blockHash) : Array.Empty<TxReceipt>();
            }

            return receipts;
        }

        public BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse)
        {
            return _blockTree.FindHeaders(hash, numberOfBlocks, skip, reverse);
        }

        public byte[][] GetNodeData(IList<Keccak> keys)
        {
            var values = new byte[keys.Count][];
            for (int i = 0; i < keys.Count; i++)
            {
                IDb stateDb = _stateDb.Innermost;
                IDb codeDb = _codeDb.Innermost;
                values[i] = stateDb.Get(keys[i]) ?? codeDb.Get(keys[i]);
            }

            return values;
        }

        public Block Find(Keccak hash) => _blockTree.FindBlock(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);

        public Keccak FindHash(long number)
        {
            try
            {
                Keccak hash = _blockTree.FindHash(number);
                return hash;
            }
            catch (Exception)
            {
                _logger.Debug("Could not handle a request for block by number since multiple blocks are available at the level and none is marked as canonical. (a fix is coming)");
            }

            return null;
        }

        private Random _broadcastRandomizer = new Random();

        [Todo(Improve.Refactor, "This may not be desired if the other node is just syncing now too")]
        private void OnNewHeadBlock(object sender, BlockEventArgs blockEventArgs)
        {
            Block block = blockEventArgs.Block;
            if (_blockTree.BestKnownNumber > block.Number) return;

            int peerCount = _pool.PeerCount;
            double broadcastRatio = Math.Sqrt(peerCount) / peerCount;

            int counter = 0;
            foreach (PeerInfo peerInfo in _pool.AllPeers)
            {
                if (peerInfo.TotalDifficulty < (block.TotalDifficulty ?? UInt256.Zero))
                {
                    if (_broadcastRandomizer.NextDouble() < broadcastRatio)
                    {
                        peerInfo.SyncPeer.SendNewBlock(block);
                        counter++;
                    }
                    else
                    {
                        peerInfo.SyncPeer.HintNewBlock(block.Hash, block.Number);
                    }
                }
            }

            if (counter > 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Broadcasting block {block.ToString(Block.Format.Short)} to {counter} peers.");
            }
        }

        public void Dispose()
        {
            _blockTree.NewHeadBlock -= OnNewHeadBlock;
        }
    }
}