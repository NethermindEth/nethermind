// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
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
        private readonly NewHeadPayloadCache _payloads;

        [ConstructorWithSideEffect]
        public NewHeadSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IBlockTree? blockTree,
            ILogManager? logManager,
            ISpecProvider specProvider,
            IBlockForRpcFactory blockForRpcFactory,
            TransactionsOption? options = null)
            : this(jsonRpcDuplexClient, blockTree, logManager, new NewHeadPayloadCache(specProvider, blockForRpcFactory), options)
        {
        }

        [ConstructorWithSideEffect]
        internal NewHeadSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IBlockTree? blockTree,
            ILogManager? logManager,
            NewHeadPayloadCache payloads,
            TransactionsOption? options = null)
            : base(jsonRpcDuplexClient, MaxQueuedBlocks)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger<NewHeadSubscription>() ?? throw new ArgumentNullException(nameof(logManager));
            _includeTransactions = options?.IncludeTransactions ?? false;
            _payloads = payloads;

            _blockTree.BlockAddedToMain += OnBlockAddedToMain;
            if (_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} will track BlockAddedToMain");
        }

        private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e) => ScheduleAction(async () =>
        {
            using JsonRpcResult result = CreateSubscriptionMessage(_payloads.Get(e.Block, _includeTransactions));
            await JsonRpcDuplexClient.SendJsonRpcResult(result);
            if (_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} printed new block");
        });

        public override string Type => SubscriptionType.EthSubscription.NewHeads;

        public override void Dispose()
        {
            _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} will no longer track BlockAddedToMain");
        }
    }
}
