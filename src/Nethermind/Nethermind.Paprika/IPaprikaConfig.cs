// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Paprika;

[ConfigCategory(HiddenFromDocs = true)]
public interface IPaprikaConfig : IConfig
{
    [ConfigItem(Description = "The depths of reads that should be cached for State data. If a read takes more than this number blocks to traverse, try to cache it", DefaultValue = "8")]
    public ushort CacheStateBeyond { get; set; }

    [ConfigItem(Description = "The total budget of entries to be used per block for state", DefaultValue  = "2000")]
    public int CacheStatePerBlock { get; set; }

    [ConfigItem(Description = "The depths of reads that should be cached for Merkle data. If a read takes more than this number blocks to traverse, try to cache it", DefaultValue = "8")]
    public ushort CacheMerkleBeyond { get; set; }

    [ConfigItem(Description = "The total budget of entries to be used per block", DefaultValue  = "2000")]
    public int CacheMerklePerBlock { get; set; }
}

public class PaprikaConfig : IPaprikaConfig
{
    public ushort CacheStateBeyond { get; set; } = 16;
    public int CacheStatePerBlock { get; set; } = 2000;

    public ushort CacheMerkleBeyond { get; set; } = 16;
    public int CacheMerklePerBlock { get; set; } = 2000;
}
