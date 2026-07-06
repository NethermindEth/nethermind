// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    [DebuggerDisplay("Main: {HasBlockOnMainChain}, Blocks: {BlockInfos.Length}")]
    public class ChainLevelInfo(bool hasBlockInMainChain, params BlockInfo[] blockInfos)
    {
        private const int NotFound = -1;

        public bool HasNonBeaconBlocks => BlockInfos.Any(static b => (b.Metadata & (BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody)) == 0);
        public bool HasBeaconBlocks => BlockInfos.Any(static b => (b.Metadata & (BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody)) != 0);
        public bool HasBlockOnMainChain { get; set; } = hasBlockInMainChain;
        public BlockInfo[] BlockInfos { get; set; } = blockInfos;
        public BlockInfo? MainChainBlock => HasBlockOnMainChain ? BlockInfos[0] : null;

        // ToDo we need to rethink this code
        public BlockInfo? BeaconMainChainBlock
        {
            get
            {
                if (BlockInfos.Length == 0)
                    return null;

                for (int i = 0; i < BlockInfos.Length; ++i)
                {
                    BlockInfo blockInfo = BlockInfos[i];
                    bool isBeaconChainMetadata = (blockInfo.Metadata & BlockMetadata.BeaconMainChain) != 0;
                    if (isBeaconChainMetadata)
                        return blockInfo;
                }

                // Note: The first block info is main
                return BlockInfos[0];
            }
        }

        public int? FindBlockInfoIndex(Hash256 blockHash)
        {
            for (int i = 0; i < BlockInfos.Length; i++)
            {
                Hash256 hashAtIndex = BlockInfos[i].BlockHash;
                if (hashAtIndex.Equals(blockHash))
                {
                    return i;
                }
            }

            return null;
        }

        public int? FindIndex(Hash256 blockHash)
        {
            for (int i = 0; i < BlockInfos.Length; i++)
            {
                Hash256 hashAtIndex = BlockInfos[i].BlockHash;
                if (hashAtIndex.Equals(blockHash))
                {
                    return i;
                }
            }

            return null;
        }

        private bool TryFindBeaconMainChainIndex(out int index)
        {
            for (int i = 0; i < BlockInfos.Length; i++)
            {
                if (BlockInfos[i].IsBeaconMainChain)
                {
                    index = i;
                    return true;
                }
            }

            index = NotFound;
            return false;
        }

        public BlockInfo? FindBlockInfo(Hash256 blockHash)
        {
            int? index = FindIndex(blockHash);
            return index.HasValue ? BlockInfos[index.Value] : null;
        }

        public void InsertBlockInfo(Hash256 hash, BlockInfo blockInfo, bool setAsMain)
        {
            BlockInfo[] blockInfos = BlockInfos;

            int? foundIndex = FindIndex(hash);
            if (foundIndex is null)
            {
                Array.Resize(ref blockInfos, blockInfos.Length + 1);
            }
            else
            {
                // Metadata flags are recorded by independent writers (e.g. beacon sync inserting the same
                // hash that FindHeader concurrently created a level entry for); an upsert that lacks a flag
                // must not erase it. Intentional clearing mutates level entries directly, never through here.
                blockInfo.Metadata |= blockInfos[foundIndex.Value].Metadata;

                if (blockInfo.EqualsIgnoringWasProcessed(blockInfos[foundIndex.Value]))
                    blockInfo.WasProcessed |= blockInfos[foundIndex.Value].WasProcessed;
            }

            int index = foundIndex ?? blockInfos.Length - 1;

            if (setAsMain)
            {
                blockInfos[index] = blockInfos[0];
                blockInfos[0] = blockInfo;
            }
            // prioritise new beacon info from beacon sync over old fcu
            else if (blockInfo.IsBeaconMainChain && TryFindBeaconMainChainIndex(out int beaconMainChainIndex))
            {
                blockInfos[index] = blockInfos[beaconMainChainIndex];
                blockInfos[beaconMainChainIndex] = blockInfo;
            }
            else
            {
                blockInfos[index] = blockInfo;
            }

            BlockInfos = blockInfos;
        }

        public void SwapToMain(int index) => (BlockInfos[index], BlockInfos[0]) = (BlockInfos[0], BlockInfos[index]);
    }
}
