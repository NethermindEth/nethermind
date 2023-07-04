// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Int256;

namespace Nethermind.Consensus;

public class MiningConfig : IMiningConfig
{
    public bool Enabled { get; set; }

    public long? TargetBlockGasLimit
    {
        get
        {
            return BlocksConfig.TargetBlockGasLimit;
        }
        set
        {
            BlocksConfig.TargetBlockGasLimit = value;
        }
    }

    public UInt256 MinGasPrice
    {
        get
        {
            return BlocksConfig.MinGasPrice;
        }
        set
        {
            BlocksConfig.MinGasPrice = value;
        }
    }

    public bool RandomizedBlocks
    {
        get
        {
            return BlocksConfig.RandomizedBlocks;
        }
        set
        {
            BlocksConfig.RandomizedBlocks = value;
        }
    }

    public string ExtraData
    {
        get
        {
            return BlocksConfig.ExtraData;
        }
        set
        {
            BlocksConfig.ExtraData = value;
        }
    }

    private IBlocksConfig? _blocksConfig = null;

    public IBlocksConfig? BlocksConfig
    {
        get
        {
            // Lazt initalisation due to the awaiting of interface defaults application on assembly
            _blocksConfig ??= new BlocksConfig();

            return _blocksConfig;
        }
    }
}
