// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Hive;

[ConfigCategory(Description = "These settings are only needed when testing with Ethereum Foundation Hive.")]
public interface IHiveConfig : IConfig
{
    [ConfigItem(Description = "The path to the test chain spec file.", DefaultValue = "chain.rlp")]
    string ChainFile { get; set; }

    [ConfigItem(Description = "The path to the directory with additional blocks.", DefaultValue = "blocks")]
    string BlocksDir { get; set; }

    [ConfigItem(Description = "The path to the keystore directory.", DefaultValue = "/keys")]
    string KeysDir { get; set; }

    [ConfigItem(Description = "Whether to enable Hive for debugging.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The path to the genesis block file.", DefaultValue = "/genesis.json")]
    string GenesisFilePath { get; set; }
}
