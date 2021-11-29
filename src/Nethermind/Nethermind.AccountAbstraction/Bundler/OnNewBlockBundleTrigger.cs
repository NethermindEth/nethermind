using System;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class OnNewBlockBundleTrigger : IBundleTrigger
    {
        private readonly IBlockTree _blockTree;

        public event EventHandler<BundleUserOpsEventArgs>? TriggerBundle;

        public OnNewBlockBundleTrigger(IBlockTree blockTree)
        {
            _blockTree = blockTree;
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        }

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            TriggerBundle?.Invoke(this, new BundleUserOpsEventArgs(e.Block));
        }
    }
}
