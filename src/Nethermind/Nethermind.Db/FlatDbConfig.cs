// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db;

public interface IFlatDbConfig: IConfig
{
    [ConfigItem(Description = "Enabled", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Import from pruning trie state db", DefaultValue = "false")]
    bool ImportFromPruningTrieState { get; set; }
}

public class FlatDbConfig: IFlatDbConfig
{
    public bool Enabled { get; set; } = false;
    public bool ImportFromPruningTrieState { get; set; } = false;
}
