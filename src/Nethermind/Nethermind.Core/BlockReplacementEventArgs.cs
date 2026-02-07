// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public class BlockReplacementEventArgs : BlockEventArgs
    {
        public BlockReplacementEventArgs(Block block)
            : this(block, null)
        {
        }

        public BlockReplacementEventArgs(Block block, Block? previousBlock)
            : this(block, previousBlock, isPartOfMainChainUpdate: false, isLastInMainChainUpdate: false, mainChainUpdateId: 0)
        {
        }

        public BlockReplacementEventArgs(
            Block block,
            Block? previousBlock,
            bool isPartOfMainChainUpdate,
            bool isLastInMainChainUpdate,
            long mainChainUpdateId) : base(block)
        {
            PreviousBlock = previousBlock;
            IsPartOfMainChainUpdate = isPartOfMainChainUpdate;
            IsLastInMainChainUpdate = isLastInMainChainUpdate;
            MainChainUpdateId = mainChainUpdateId;
        }

        public Block? PreviousBlock { get; }

        public bool IsPartOfMainChainUpdate { get; }

        public bool IsLastInMainChainUpdate { get; }

        public long MainChainUpdateId { get; }
    }
}
