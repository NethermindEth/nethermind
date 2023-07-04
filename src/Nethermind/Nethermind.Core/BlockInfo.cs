// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [Flags]
    public enum BlockMetadata
    {
        None = 0x0,
        Finalized = 1,
        Invalid = 2,
        BeaconHeader = 4,
        BeaconBody = 8,
        BeaconMainChain = 16
    }

    public class BlockInfo
    {
        public BlockInfo(Keccak blockHash, in UInt256 totalDifficulty, BlockMetadata metadata = BlockMetadata.None)
        {
            BlockHash = blockHash;
            TotalDifficulty = totalDifficulty;
            Metadata = metadata;
        }

        public UInt256 TotalDifficulty { get; set; }

        public bool WasProcessed { get; set; }

        public Keccak BlockHash { get; }

        public bool IsFinalized
        {
            get => (Metadata & BlockMetadata.Finalized) == BlockMetadata.Finalized;
            set
            {
                if (value)
                {
                    Metadata |= BlockMetadata.Finalized;
                }
                else
                {
                    Metadata &= ~BlockMetadata.Finalized;
                }
            }
        }


        public bool IsBeaconHeader
        {
            get => (Metadata & BlockMetadata.BeaconHeader) != 0;
        }

        public bool IsBeaconBody
        {
            get => (Metadata & BlockMetadata.BeaconBody) != 0;
        }

        public bool IsBeaconMainChain
        {
            get => (Metadata & BlockMetadata.BeaconMainChain) != 0;
        }

        public bool IsBeaconInfo
        {
            get => (Metadata & (BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader)) != 0;
        }

        public BlockMetadata Metadata { get; set; }

        /// <summary>
        /// This property is not serialized
        /// </summary>
        public long BlockNumber { get; set; }

        public override string ToString() => BlockHash.ToString();
    }
}
