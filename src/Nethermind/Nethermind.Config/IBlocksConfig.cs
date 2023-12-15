// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Config;

public interface IBlocksConfig : IConfig
{

    //[ConfigItem(
    //    Description = "Defines whether the blocks should be produced.",
    //    DefaultValue = "false")]
    //bool Enabled { get; set; }

    [ConfigItem(
        Description = "The block gas limit that the block producer should try to reach in the fastest possible way based on the protocol rules. If not specified, then the block producer should follow others.",
        DefaultValue = "null")]
    long? TargetBlockGasLimit { get; set; }

    [ConfigItem(
        Description = "The minimum gas premium (or the gas price before the London hard fork) for transactions accepted by the block producer.",
        DefaultValue = "1")]
    UInt256 MinGasPrice { get; set; }

    [ConfigItem(
        Description = "Whether to change the difficulty of the block randomly within the constraints. Used in NethDev only.",
        DefaultValue = "false")]
    bool RandomizedBlocks { get; set; }

    [ConfigItem(Description = "The block header extra data up to 32 bytes in length.", DefaultValue = "Nethermind")]
    string ExtraData { get; set; }

    [ConfigItem(Description = "The block time slot, in seconds.", DefaultValue = "12")]
    ulong SecondsPerSlot { get; set; }

    byte[] GetExtraDataBytes();
}
