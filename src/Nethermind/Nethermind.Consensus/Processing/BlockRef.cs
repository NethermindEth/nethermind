// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing
{
    internal class BlockRef
    {
        public BlockRef(Block block, ProcessingOptions processingOptions = ProcessingOptions.None)
        {
            Block = block;
            ProcessingOptions = processingOptions;
            IsInDb = false;
            BlockHash = block.Hash!;
        }

        public BlockRef(Keccak blockHash, ProcessingOptions processingOptions = ProcessingOptions.None)
        {
            Block = null;
            IsInDb = true;
            BlockHash = blockHash;
            ProcessingOptions = processingOptions;
        }

        public bool IsInDb { get; set; }
        public Keccak BlockHash { get; set; }
        public Block? Block { get; set; }
        public ProcessingOptions ProcessingOptions { get; }

        public bool Resolve(IBlockTree blockTree)
        {
            if (IsInDb)
            {
                Block? block = blockTree.FindBlock(BlockHash!, BlockTreeLookupOptions.None);
                if (block is null)
                {
                    return false;
                }

                Block = block;
                IsInDb = false;
            }

            return true;
        }

        public override string ToString() => Block?.ToString() ?? BlockHash.ToString();
    }
}
