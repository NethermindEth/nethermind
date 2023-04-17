// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    [DebuggerDisplay("Main: {HasBlockOnMainChain}, Blocks: {BlockInfos.Length}")]
    public class ChainLevelInfo // TODO: move to blockchain namespace
    {
        public ChainLevelInfo(bool hasBlockInMainChain, params BlockInfo[] blockInfos)
        {
            HasBlockOnMainChain = hasBlockInMainChain;
            BlockInfos = blockInfos;
        }

        public bool HasNonBeaconBlocks => BlockInfos.Any(b => (b.Metadata & (BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody)) == 0);
        public bool HasBeaconBlocks => BlockInfos.Any(b => (b.Metadata & (BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody)) != 0);
        public bool HasBlockOnMainChain { get; set; }
        public BlockInfo[] BlockInfos { get; set; }
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

        public int? FindBlockInfoIndex(Keccak blockHash)
        {
            for (int i = 0; i < BlockInfos.Length; i++)
            {
                Keccak hashAtIndex = BlockInfos[i].BlockHash;
                if (hashAtIndex.Equals(blockHash))
                {
                    return i;
                }
            }

            return null;
        }
    }
}
