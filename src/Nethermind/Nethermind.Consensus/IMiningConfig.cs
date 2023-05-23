// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Int256;

namespace Nethermind.Consensus;

public interface IMiningConfig : IConfig
{
    [ConfigItem(
        Description = "Defines whether the blocks should be produced.",
        DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(
        Description = "Deprecated since v1.14.6. Please use Blocks.TargetBlockGasLimit. " +
                      "Values you set here are forwarded to it. " +
                      "Conflicting values will cause Exceptions. " +
                      "Block gas limit that the block producer should try to reach in the fastest " +
                      "possible way based on protocol rules. " +
                      "NULL value means that the miner should follow other miners.",
        DefaultValue = "null")]
    long? TargetBlockGasLimit { get; set; }

    [ConfigItem(
        Description = "Deprecated since v1.14.6. Please use Blocks.MinGasPrice " +
                      "Values you set here are forwarded to it. " +
                      "Conflicting values will cause Exceptions. " +
                      "Minimum gas premium for transactions accepted by the block producer. " +
                      "Before EIP1559: Minimum gas price for transactions accepted by the block producer.",
        DefaultValue = "1")]
    UInt256 MinGasPrice { get; set; }

    [ConfigItem(
        Description = "Deprecated since v1.14.6. Please use Blocks.RandomizedBlocks " +
                      "Values you set here are forwarded to it. " +
                      "Conflicting values will cause Exceptions. " +
                      "Only used in NethDev. Setting this to true will change the difficulty " +
                      "of the block randomly within the constraints.",
        DefaultValue = "false")]
    bool RandomizedBlocks { get; set; }

    [ConfigItem(Description = "Deprecated since v1.14.6. Please use Blocks.ExtraData" +
                              "Values you set here are forwarded to it. " +
                              "Conflicting values will cause Exceptions. " +
                              "Block header extra data. 32-bytes shall be extra data max length.",
        DefaultValue = "Nethermind")]
    string ExtraData { get; set; }

    [ConfigItem(HiddenFromDocs = true, DisabledForCli = true, DefaultValue = "null")]
    IBlocksConfig? BlocksConfig { get; }
}
