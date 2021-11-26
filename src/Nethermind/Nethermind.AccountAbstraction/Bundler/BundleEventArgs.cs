using System;
using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class BundleEventArgs : EventArgs
    {
        public Block Head { get; private set; }

        public BundleEventArgs(Block head)
        {
            Head = head;
        }
    }
}
