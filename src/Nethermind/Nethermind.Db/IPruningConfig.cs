// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db;

[ConfigCategory(Description = "Configuration of the fallback maximum blockchain reorganization depth used by the block tree and log index.")]
public interface IPruningConfig : IConfig
{
    [ConfigItem(Description = "Fallback maximum reorganization depth used by the block tree and log index when no explicit value is configured.", DefaultValue = "64")]
    ulong PruningBoundary { get; set; }
}
