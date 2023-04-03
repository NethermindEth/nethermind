// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class NewHeadSubscription : Subscription
    {
        private readonly IBlockTree _blockTree;
        private readonly bool _includeTransactions;
        private readonly ISpecProvider _specProvider;


        public NewHeadSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient,
            IBlockTree? blockTree,
            ILogManager? logManager,
            ISpecProvider specProvider,
            TransactionsOption? options = null)
            : base(jsonRpcDuplexClient)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _includeTransactions = options?.IncludeTransactions ?? false;
            _specProvider = specProvider;

            _blockTree.BlockAddedToMain += OnBlockAddedToMain;
            if (_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} will track BlockAddedToMain");
        }

        private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
        {
            ScheduleAction(() =>
            {
                JsonRpcResult result = CreateSubscriptionMessage(new BlockForRpc(e.Block, _includeTransactions, _specProvider));

                JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} printed new block");
            });
        }

        public override string Type => SubscriptionType.NewHeads;

        public override void Dispose()
        {
            _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
            base.Dispose();
            if (_logger.IsTrace) _logger.Trace($"NewHeads subscription {Id} will no longer track BlockAddedToMain");
        }
    }
}
