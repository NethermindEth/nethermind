// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db;

[ConfigCategory(Description = "Configuration of the fallback reorganization depth of the log index. State retention is configured with FlatDb.MinReorgDepth and FlatDb.MaxReorgDepth.")]
public interface IPruningConfig : IConfig
{
    [ConfigItem(Description = "Fallback for LogIndex.MaxReorgDepth when that is not set. It no longer bounds how much state is kept.", DefaultValue = "64")]
    ulong PruningBoundary { get; set; }
}
