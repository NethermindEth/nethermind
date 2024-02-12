// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Paprika;

[ConfigCategory(HiddenFromDocs = true)]
public interface IPaprikaConfig : IConfig
{
    [ConfigItem(Description = "The depths of reads that should be cached by Paprika. If a read takes more than this number blocks to traverse, try to cache it", DefaultValue = PaprikaConfig.DefaultCacheReadsBeyond)]
    public ushort CacheBeyond { get; set; }

    [ConfigItem(Description = "The total budget of entries to be used per block", DefaultValue  = PaprikaConfig.DefaultCacheEntriesPerBlock)]
    public int CachePerBlock { get; set; }
}

public class PaprikaConfig : IPaprikaConfig
{
    public const string DefaultCacheReadsBeyond = "16";

    public ushort CacheBeyond { get; set; } = ushort.Parse(DefaultCacheReadsBeyond);

    public const string DefaultCacheEntriesPerBlock = "2000";

    public int CachePerBlock { get; set; } = int.Parse(DefaultCacheEntriesPerBlock);
}
