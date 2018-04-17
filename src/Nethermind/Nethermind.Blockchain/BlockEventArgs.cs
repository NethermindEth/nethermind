using System;
using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public class BlockEventArgs : EventArgs
    {
        public Block Block { get; }

        public BlockEventArgs(Block block)
        {
            Block = block;
        }
    }
}