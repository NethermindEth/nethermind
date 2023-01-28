// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public class BlockReplacementEventArgs : BlockEventArgs
    {
        public Block? PreviousBlock { get; }

        public BlockReplacementEventArgs(Block block, Block? previousBlock = null) : base(block)
        {
            PreviousBlock = previousBlock;
        }
    }
}
