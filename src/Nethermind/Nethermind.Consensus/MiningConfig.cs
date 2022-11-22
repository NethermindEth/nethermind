//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            if (_blocksConfig is null)
            {
                _blocksConfig = new BlocksConfig();
            }

            return _blocksConfig;
        }
    }
}
