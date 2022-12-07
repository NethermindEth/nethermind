// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Config
{
    public interface IBlocksConfig : IConfig
    {

        //[ConfigItem(
        //    Description = "Defines whether the blocks should be produced.",
        //    DefaultValue = "false")]
        //bool Enabled { get; set; }

        [ConfigItem(
            Description = "Block gas limit that the block producer should try to reach in the fastest possible way based on protocol rules." +
                          " NULL value means that the miner should follow other miners.",
            DefaultValue = "null")]
        long? TargetBlockGasLimit { get; set; }

        [ConfigItem(
            Description = "Minimum gas premium for transactions accepted by the block producer. Before EIP1559: Minimum gas price for transactions accepted by the block producer.",
            DefaultValue = "1")]
        UInt256 MinGasPrice { get; set; }

        [ConfigItem(
            Description = "Only used in NethDev. Setting this to true will change the difficulty of the block randomly within the constraints.",
            DefaultValue = "false")]
        bool RandomizedBlocks { get; set; }

        [ConfigItem(Description = "Block header extra data. 32-bytes shall be extra data max length.", DefaultValue = "Nethermind")]
        string ExtraData { get; set; }

        [ConfigItem(Description = "Seconds per slot.", DefaultValue = "12")]
        ulong SecondsPerSlot { get; set; }

        byte[] GetExtraDataBytes();
    }
}
