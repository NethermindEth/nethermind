// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Hive
{
    [ConfigCategory(Description = "These items need only be set when testing with Hive (Ethereum Foundation tool)")]
    public interface IHiveConfig : IConfig
    {
        [ConfigItem(Description = "Path to a file with a test chain definition.", DefaultValue = "\"/chain.rlp\"")]
        string ChainFile { get; set; }

        [ConfigItem(Description = "Path to a directory with additional blocks.", DefaultValue = "\"/blocks\"")]
        string BlocksDir { get; set; }

        [ConfigItem(Description = "Path to a test key store directory.", DefaultValue = "\"/keys\"")]
        string KeysDir { get; set; }

        [ConfigItem(Description = "Enabling hive for debugging purpose", DefaultValue = "false")]
        bool Enabled { get; set; }

        [ConfigItem(Description = "Path to genesis block.", DefaultValue = "\"/genesis.json\"")]
        string GenesisFilePath { get; set; }
    }
}
