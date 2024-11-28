// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Paprika;

[ConfigCategory(HiddenFromDocs = true)]
public interface IPaprikaConfig : IConfig
{
    [ConfigItem(Description = "The depths of reads that should be cached for State data. If a read takes more than this number blocks to traverse, try to cache it", DefaultValue = $"65000")]
    public ushort CacheStateBeyond { get; set; }

    [ConfigItem(Description = "The total budget of entries to be used per block for state", DefaultValue = "0")]
    public int CacheStatePerBlock { get; set; }

    [ConfigItem(Description = "The depths of reads that should be cached for Merkle data. If a read takes more than this number blocks to traverse, try to cache it", DefaultValue = "65000")]
    public ushort CacheMerkleBeyond { get; set; }

    [ConfigItem(Description = "The total budget of entries to be used per block", DefaultValue = "0")]
    public int CacheMerklePerBlock { get; set; }

    [ConfigItem(Description = "Whether Merkle should use parallelism", DefaultValue = "true")]
    public bool ParallelMerkle { get; set; }

    [ConfigItem(Description = "Whether a prefetcher should be run in the background to prefetch Merkle nodes that are on the path of the data access", DefaultValue = "true")]
    public bool Prefetch { get; set; }

    [ConfigItem(Description = "Paprika history depth", DefaultValue = "32")]
    public int HistoryDepth { get; set; }

    [ConfigItem(Description = "Size in GB", DefaultValue = "512")]
    public int SizeInGb { get; set; }
}
