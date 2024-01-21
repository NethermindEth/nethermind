// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class OnNewBlockBundleTrigger : IBundleTrigger
    {
        private readonly IBlockTree _blockTree;
        private readonly Logger _logger;

        public event EventHandler<BundleUserOpsEventArgs>? TriggerBundle;

        public OnNewBlockBundleTrigger(IBlockTree blockTree, in Logger logger)
        {
            _logger = logger;
            _blockTree = blockTree;
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        }

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            TriggerBundle?.Invoke(this, new BundleUserOpsEventArgs(e.Block));
        }
    }
}
