using System;
using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class BundleUserOpsEventArgs : EventArgs
    {
        public Block Head { get; private set; }

        public BundleUserOpsEventArgs(Block head)
        {
            Head = head;
        }
    }
}
