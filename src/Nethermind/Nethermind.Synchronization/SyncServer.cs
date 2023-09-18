// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.LesSync;
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
        private readonly IReadOnlyKeyValueStore _stateDb;
        private readonly IReadOnlyKeyValueStore _codeDb;
        private readonly IWitnessRepository _witnessRepository;
        private readonly IGossipPolicy _gossipPolicy;
        private readonly ISpecProvider _specProvider;
        private readonly CanonicalHashTrie? _cht;
        private bool _gossipStopped = false;
        private readonly Random _broadcastRandomizer = new();

        private readonly LruCache<ValueKeccak, ISyncPeer> _recentlySuggested = new(128, 128, "recently suggested blocks");

        private readonly long _pivotNumber;
        private readonly Keccak _pivotHash;
        private BlockHeader? _pivotHeader;

        public SyncServer(
            IReadOnlyKeyValueStore stateDb,
            IReadOnlyKeyValueStore codeDb,
            IBlockTree blockTree,
            IReceiptFinder receiptFinder,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncPeerPool pool,
            ISyncModeSelector syncModeSelector,
            ISyncConfig syncConfig,
            IWitnessRepository? witnessRepository,
            IGossipPolicy gossipPolicy,
            ISpecProvider specProvider,
            ILogManager logManager,
            CanonicalHashTrie? cht = null)
        {
            ISyncConfig config = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _witnessRepository = witnessRepository ?? throw new ArgumentNullException(nameof(witnessRepository));
            _gossipPolicy = gossipPolicy ?? throw new ArgumentNullException(nameof(gossipPolicy));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _cht = cht;
            _pivotNumber = config.PivotNumberParsed;
            _pivotHash = new Keccak(config.PivotHash ?? Keccak.Zero.ToString());

            _blockTree.NewHeadBlock += OnNewHeadBlock;
            _pool.NotifyPeerBlock += OnNotifyPeerBlock;
        }

        public ulong NetworkId => _blockTree.NetworkId;
        public BlockHeader Genesis => _blockTree.Genesis;

        public BlockHeader? Head
        {
            get
            {
                if (_blockTree.Head is null)
                {
                    return null;
                }

                bool headIsGenesis = _blockTree.Head.Hash == _blockTree.Genesis.Hash;
                if (headIsGenesis)
                {
                    _pivotHeader ??= _blockTree.FindHeader(_pivotHash, BlockTreeLookupOptions.None);
                }

                return headIsGenesis
                    ? _pivotHeader ?? _blockTree.Genesis
                    : _blockTree.Head?.Header;
            }
        }

        public Keccak[]? GetBlockWitnessHashes(Keccak blockHash)
        {
            return _witnessRepository.Load(blockHash);
        }

        public int GetPeerCount()
        {
            return _pool.PeerCount;
        }

        private readonly Guid _sealValidatorUserGuid = Guid.NewGuid();

        public void AddNewBlock(Block block, ISyncPeer nodeWhoSentTheBlock)
        {
            if (!_gossipPolicy.CanGossipBlocks) return;
            if (block.Difficulty == 0) return; // don't gossip post merge blocks

            if (block.TotalDifficulty is null)
            {
                throw new InvalidDataException("Cannot add a block with unknown total difficulty");
            }

            if (block.Hash is null)
            {
                throw new InvalidDataException("Cannot add a block with unknown hash");
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

            // We skip blocks that are old
            if (isBlockBeforeTheSyncPivot || isBlockOlderThanMaxReorgAllows)
            {
                return;
            }

            // We skip already imported blocks
            if (_blockTree.IsKnownBlock(block.Number, block.Hash))
            {
                return;
            }

            if (_recentlySuggested.Set(block.Hash, nodeWhoSentTheBlock))
            {
                if (_specProvider.TerminalTotalDifficulty is not null && block.TotalDifficulty >= _specProvider.TerminalTotalDifficulty)
                {
                    if (_logger.IsInfo) _logger.Info($"Peer {nodeWhoSentTheBlock} sent block {block} with total difficulty {block.TotalDifficulty} higher than TTD {_specProvider.TerminalTotalDifficulty}");
                }

                Block? parent = _blockTree.FindBlock(block.ParentHash);
                if (parent is not null)
                {
                    // we null total difficulty for a block in a block tree as we don't trust the message
                    UInt256? totalDifficulty = block.TotalDifficulty;

                    // Recalculate total difficulty as we don't trust total difficulty from gossip
                    block.Header.TotalDifficulty = parent.TotalDifficulty + block.Header.Difficulty;
                    if (!_blockValidator.ValidateSuggestedBlock(block))
                    {
                        ThrowOnInvalidBlock(block, nodeWhoSentTheBlock);
                    }

                    if (!ValidateSeal(block, nodeWhoSentTheBlock))
                    {
                        ThrowOnInvalidBlock(block, nodeWhoSentTheBlock);
                    }

                    // we want to broadcast original block, lets check if TD changed
                    Block blockToBroadCast = totalDifficulty == block.TotalDifficulty
                        ? block
                        : new(block.Header.Clone(), block.Body) { Header = { TotalDifficulty = totalDifficulty } };

                    BroadcastBlock(blockToBroadCast, false, nodeWhoSentTheBlock);

                    SyncMode syncMode = _syncModeSelector.Current;
                    bool notInFastSyncNorStateSync = (syncMode & (SyncMode.FastSync | SyncMode.StateNodes)) == SyncMode.None;
                    bool inFullSyncOrWaitingForBlocks = (syncMode & (SyncMode.Full | SyncMode.WaitingForBlock)) != SyncMode.None;
                    if (notInFastSyncNorStateSync || inFullSyncOrWaitingForBlocks)
                    {
                        LogBlockAuthorNicely(block, nodeWhoSentTheBlock);
                        SyncBlock(block, nodeWhoSentTheBlock);
                    }
                }
                else
                {
                    LogBlockAuthorNicely(block, nodeWhoSentTheBlock);
                    if (_logger.IsDebug) _logger.Debug($"Peer {nodeWhoSentTheBlock} sent block with unknown parent {block}, best suggested {_blockTree.BestSuggestedHeader}.");
                }
            }
        }

        private void ThrowOnInvalidBlock(Block block, ISyncPeer nodeWhoSentTheBlock)
        {
            string message = $"Peer {nodeWhoSentTheBlock.Node:c} sent an invalid block.";
            if (_logger.IsDebug) _logger.Debug(message);
            _recentlySuggested.Delete(block.Hash!);
            throw new EthSyncException(message);
        }

        private bool ValidateSeal(Block block, ISyncPeer syncPeer)
        {
            if (_logger.IsTrace) _logger.Trace($"Validating seal of {block.ToString(Block.Format.Short)}) from {syncPeer:c}");

            // We hint validation range mostly to help ethash to cache epochs.
            // It is important that we only do that here, after we ensured that the block is
            // in the range of [Head - MaxReorganizationLength, Head].
            // Otherwise we could hint incorrect ranges and cause expensive cache recalculations.
            _sealValidator.HintValidationRange(_sealValidatorUserGuid, block.Number - 128, block.Number + 1024);
            return _sealValidator.ValidateSeal(block.Header, true);
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

        private void SyncBlock(Block block, ISyncPeer nodeWhoSentTheBlock)
        {
            bool shouldSkipProcessing = _blockTree.Head.IsPoS() || block.IsPostMerge;
            if (shouldSkipProcessing)
            {
                if (_logger.IsInfo) _logger.Info($"Skipped processing of discovered block {block}, block.IsPostMerge: {block.IsPostMerge}, current head: {_blockTree.Head}");
            }

            if (_logger.IsTrace) _logger.Trace($"SyncServer SyncPeer {nodeWhoSentTheBlock} SuggestBlock BestSuggestedBlock {_blockTree.BestSuggestedBody}, BestSuggestedBlock TD {_blockTree.BestSuggestedBody?.TotalDifficulty}, Block TD {block.TotalDifficulty}, Head: {_blockTree.Head}, Head: {_blockTree.Head?.TotalDifficulty}  Block {block.ToString(Block.Format.FullHashAndNumber)}");
            AddBlockResult result = _blockTree.SuggestBlock(block, shouldSkipProcessing ? BlockTreeSuggestOptions.ForceDontSetAsMain : BlockTreeSuggestOptions.ShouldProcess);
            if (_logger.IsTrace) _logger.Trace($"SyncServer block {block.ToString(Block.Format.FullHashAndNumber)}, SuggestBlock result: {result}.");
        }

        private void BroadcastBlock(Block block, bool allowHashes, ISyncPeer? nodeWhoSentTheBlock = null)
        {
            if (!_gossipPolicy.CanGossipBlocks) return;

            Task.Run(() =>
                {
                    double CalculateBroadcastRatio(int minPeers, int peerCount) => peerCount == 0 ? 0 : minPeers / (double)peerCount;

                    int peerCount = _pool.PeerCount - (nodeWhoSentTheBlock is null ? 0 : 1);
                    int minPeers = (int)Math.Ceiling(Math.Sqrt(peerCount));
                    double broadcastRatio = CalculateBroadcastRatio(minPeers, peerCount);
                    int counter = 0;
                    foreach (PeerInfo peerInfo in _pool.AllPeers)
                    {
                        if (nodeWhoSentTheBlock != peerInfo.SyncPeer)
                        {
                            if (_broadcastRandomizer.NextDouble() < broadcastRatio)
                            {
                                NotifyOfNewBlock(peerInfo, peerInfo.SyncPeer, block, SendBlockMode.FullBlock);
                                counter++;
                                minPeers--;
                            }
                            else if (allowHashes)
                            {
                                NotifyOfNewBlock(peerInfo, peerInfo.SyncPeer, block, SendBlockMode.HashOnly);
                            }

                            peerCount--;
                            broadcastRatio = CalculateBroadcastRatio(minPeers, peerCount);
                        }
                    }

                    if (counter > 0 && _logger.IsDebug) _logger.Debug($"Broadcasting block {block.ToString(Block.Format.Short)} to {counter} peers.");
                }
            ).ContinueWith(t => t.Exception?.Handle(ex =>
                {
                    if (_logger.IsError) _logger.Error($"Error while broadcasting block {block.ToString(Block.Format.Short)}.", ex);
                    return true;
                }),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// Code from AndreaLanfranchi - https://github.com/NethermindEth/nethermind/pull/2078
        /// Generally it tries to find the sealer / miner name.
        /// </summary>
        private void LogBlockAuthorNicely(Block block, ISyncPeer syncPeer)
        {
            StringBuilder sb = new();
            sb.Append($"Discovered new block {block.ToString(Block.Format.HashNumberAndTx)}");

            if (block.Author is not null)
            {
                sb.Append(" sealer ");
                if (KnownAddresses.GoerliValidators.TryGetValue(block.Author, out string value))
                {
                    sb.Append(value);
                }
                else if (KnownAddresses.RinkebyValidators.TryGetValue(block.Author, out value))
                {
                    sb.Append(value);
                }
                else
                {
                    sb.Append(block.Author);
                }
            }
            else if (block.Beneficiary is not null)
            {
                sb.Append(" miner ");
                if (KnownAddresses.KnownMiners.TryGetValue(block.Beneficiary, out string value))
                {
                    sb.Append(value);
                }
                else
                {
                    sb.Append(block.Beneficiary);
                }
            }

            sb.Append($", sent by {syncPeer:s}");

            if (block.Header?.AuRaStep is not null)
            {
                sb.Append($", with AuRa step {block.Header.AuRaStep.Value}");
            }

            if (_logger.IsDebug)
            {
                sb.Append($", with difficulty {block.Difficulty}/{block.TotalDifficulty}");
            }

            _logger.Info(sb.ToString());
        }

        public void HintBlock(Keccak hash, long number, ISyncPeer syncPeer)
        {
            if (!_gossipPolicy.CanGossipBlocks) return;

            if (number > syncPeer.HeadNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"HINT Updating header of {syncPeer} from {syncPeer.HeadNumber} {syncPeer.TotalDifficulty} to {number}");
                syncPeer.HeadNumber = number;
                syncPeer.HeadHash = hash;

                if (!_recentlySuggested.Contains(hash) && !_blockTree.IsKnownBlock(number, hash))
                {
                    _pool.RefreshTotalDifficulty(syncPeer, hash);
                }
            }
        }

        public TxReceipt[] GetReceipts(Keccak? blockHash)
        {
            return blockHash is not null ? _receiptFinder.Get(blockHash) : Array.Empty<TxReceipt>();
        }

        public BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse)
        {
            return _blockTree.FindHeaders(hash, numberOfBlocks, skip, reverse);
        }

        public byte[]?[] GetNodeData(IReadOnlyList<Keccak> keys, NodeDataType includedTypes = NodeDataType.State | NodeDataType.Code)
        {
            byte[]?[] values = new byte[keys.Count][];
            for (int i = 0; i < keys.Count; i++)
            {
                values[i] = null;
                if ((includedTypes & NodeDataType.State) == NodeDataType.State)
                {
                    values[i] = _stateDb[keys[i].Bytes];
                }

                if (values[i] is null && (includedTypes & NodeDataType.Code) == NodeDataType.Code)
                {
                    values[i] = _codeDb[keys[i].Bytes];
                }
            }

            return values;
        }

        public BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant)
        {
            return _blockTree.FindLowestCommonAncestor(firstDescendant, secondDescendant, Sync.MaxReorgLength);
        }

        public Block Find(Keccak hash) => _blockTree.FindBlock(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);

        public Keccak? FindHash(long number)
        {
            try
            {
                Keccak? hash = _blockTree.FindHash(number);
                return hash;
            }
            catch (Exception)
            {
                if (_logger.IsDebug) _logger.Debug("Could not handle a request for block by number since multiple blocks are available at the level and none is marked as canonical. (a fix is coming)");
            }

            return null;
        }

        [Todo(Improve.Refactor, "This may not be desired if the other node is just syncing now too")]
        private void OnNewHeadBlock(object? sender, BlockEventArgs blockEventArgs)
        {
            Block block = blockEventArgs.Block;
            if ((_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0) <= block.TotalDifficulty)
            {
                BroadcastBlock(block, true, _recentlySuggested.Get(block.Hash));
            }
        }

        private void NotifyOfNewBlock(PeerInfo? peerInfo, ISyncPeer syncPeer, Block broadcastedBlock, SendBlockMode mode)
        {
            if (!_gossipPolicy.CanGossipBlocks) return;

            try
            {
                syncPeer.NotifyOfNewBlock(broadcastedBlock, mode);
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error while broadcasting block {broadcastedBlock.ToString(Block.Format.Short)} to peer {peerInfo ?? (object)syncPeer}.", e);
            }
        }

        private void OnNotifyPeerBlock(object? sender, PeerBlockNotificationEventArgs e) => NotifyOfNewBlock(null, e.SyncPeer, e.Block, SendBlockMode.FullBlock);


        public void StopNotifyingPeersAboutNewBlocks()
        {
            if (_gossipStopped == false)
            {
                _blockTree.NewHeadBlock -= OnNewHeadBlock;
                _pool.NotifyPeerBlock -= OnNotifyPeerBlock;
                _gossipStopped = true;
            }
        }

        public void Dispose()
        {
            StopNotifyingPeersAboutNewBlocks();
        }

        private readonly object _chtLock = new();

        // TODO - Cancellation token?
        // TODO - not a fan of this function name - CatchUpCHT, AddMissingCHTBlocks, ...?
        public Task BuildCHT()
        {
            return Task.CompletedTask; // removing LES code

#pragma warning disable 162
            return Task.Run(() =>
            {
                lock (_chtLock)
                {
                    if (_cht is null)
                    {
                        throw new InvalidAsynchronousStateException("CHT reference is null when building CHT.");
                    }

                    // Note: The spec says this should be 2048, but I don't think we'd ever want it to be higher than the max reorg depth we allow.
                    long maxSection =
                        CanonicalHashTrie.GetSectionFromBlockNo(_blockTree.FindLatestHeader().Number -
                                                                Sync.MaxReorgLength);
                    long maxKnownSection = _cht.GetMaxSectionIndex();

                    for (long section = (maxKnownSection + 1); section <= maxSection; section++)
                    {
                        long sectionStart = section * CanonicalHashTrie.SectionSize;
                        for (int blockOffset = 0; blockOffset < CanonicalHashTrie.SectionSize; blockOffset++)
                        {
                            _cht.Set(_blockTree.FindHeader(sectionStart + blockOffset));
                        }

                        _cht.Commit(section);
                    }
                }
            });
#pragma warning restore 162
        }

        public CanonicalHashTrie? GetCHT()
        {
            return _cht;
        }
    }
}
