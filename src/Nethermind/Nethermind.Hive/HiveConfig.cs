// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Hive
{
    public class HiveConfig : IHiveConfig
    {
        public string ChainFile { get; set; } = "/chain.rlp";
        public string BlocksDir { get; set; } = "/blocks";
        public string KeysDir { get; set; } = "/keys";
        public bool Enabled { get; set; }
        public string GenesisFilePath { get; set; } = "/genesis.json";
    }
}
