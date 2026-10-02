// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing
{
    internal class BlockRef
    {
        private readonly ParallelUnbalancedWork.WorkerGroup? _workers;

        public BlockRef(Block block, ProcessingOptions processingOptions = ProcessingOptions.None)
        {
            Block = block;
            ProcessingOptions = processingOptions;
            IsInDb = false;
            BlockHash = block.Hash!;
        }

        public BlockRef(Hash256 blockHash, ProcessingOptions processingOptions = ProcessingOptions.None,
            ParallelUnbalancedWork.WorkerGroup? workers = null)
        {
            Block = null;
            IsInDb = true;
            BlockHash = blockHash;
            ProcessingOptions = processingOptions;
            _workers = workers;
        }

        public bool IsInDb { get; set; }
        public Hash256 BlockHash { get; set; }
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
                block.Workers ??= _workers;
                IsInDb = false;
            }

            return true;
        }

        public override string ToString() => Block?.ToString() ?? BlockHash.ToString();
    }
}
