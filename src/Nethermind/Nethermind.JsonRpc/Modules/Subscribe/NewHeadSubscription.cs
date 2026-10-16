// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class NewHeadSubscription : Subscription
    {
        private readonly IBlockTree _blockTree;
        private readonly bool _includeTransactions;
        private readonly bool _headerOnly;
        private readonly IOptimisticHeadSource? _optimisticHeads;
        private readonly NewHeadPayloadCache _payloads;

        // Read and written only by the queued sends, which one reader runs in order.
        private Hash256? _lastAnnounced;

        [ConstructorWithSideEffect]
        public NewHeadSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IBlockTree? blockTree,
            ILogManager? logManager,
            ISpecProvider specProvider,
            IBlockForRpcFactory blockForRpcFactory,
            TransactionsOption? options = null)
            : this(jsonRpcDuplexClient, blockTree, logManager, new NewHeadPayloadCache(specProvider, blockForRpcFactory), options?.IncludeTransactions ?? false)
        {
        }

        /// <param name="headerOnly">Announce the header alone; ignored with <paramref name="includeTransactions"/>.</param>
        /// <param name="optimisticHeads">
        /// When given, a block is also announced as soon as it extends the head, and not again when it becomes canonical.
        /// </param>
        [ConstructorWithSideEffect]
        internal NewHeadSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IBlockTree? blockTree,
            ILogManager? logManager,
            NewHeadPayloadCache payloads,
            bool includeTransactions,
            bool headerOnly = false,
            IOptimisticHeadSource? optimisticHeads = null)
            : base(jsonRpcDuplexClient, MaxQueuedBlocks)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger<NewHeadSubscription>() ?? throw new ArgumentNullException(nameof(logManager));
            _includeTransactions = includeTransactions;
            _headerOnly = headerOnly && !includeTransactions;
            _optimisticHeads = optimisticHeads;
            _payloads = payloads;

            _blockTree.BlockAddedToMain += OnBlockAddedToMain;
            if (_optimisticHeads is not null) _optimisticHeads.OptimisticHead += OnOptimisticHead;
            if (_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} will track BlockAddedToMain{(_optimisticHeads is null ? "" : " and optimistic heads")}");
        }

        private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e) => Announce(e.Block);

        private void OnOptimisticHead(object? sender, BlockEventArgs e) => Announce(e.Block);

        private void Announce(Block block) => ScheduleAction(async () =>
        {
            // An optimistic subscriber gets each head once: a block announced ahead of the forkchoice update is not
            // announced again when it becomes canonical, while a different block winning the height is announced as a
            // reorg. Both announcements queue here, so the order they were raised in is the order they are judged in.
            if (_optimisticHeads is not null && block.Hash is not null && block.Hash == _lastAnnounced) return;

            using JsonRpcResult result = CreateSubscriptionMessage(_payloads.Get(block, _includeTransactions, _headerOnly));
            await JsonRpcDuplexClient.SendJsonRpcResult(result);
            _lastAnnounced = block.Hash;
            if (_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} printed new block");
        });

        public override string Type => SubscriptionType.EthSubscription.NewHeads;

        public override void Dispose()
        {
            _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
            if (_optimisticHeads is not null) _optimisticHeads.OptimisticHead -= OnOptimisticHead;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} will no longer track BlockAddedToMain");
        }
    }
}
