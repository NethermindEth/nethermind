// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>The three independently folded EIP-8297 stem-trie partitions.</summary>
public enum PbtPartition : byte
{
    /// <summary>The account-header zone, rooted at prefix <c>0000</c>.</summary>
    Account,

    /// <summary>The code-overflow zone, rooted at prefix <c>0001</c>.</summary>
    Code,

    /// <summary>All storage zones, rooted at prefix <c>1</c>.</summary>
    Storage,
}

/// <summary>Routing and fixed-prefix information for the three stem-trie partitions.</summary>
public static class PbtPartitions
{
    public const int Count = 3;

    /// <summary>The partitions in state-root order.</summary>
    public static ReadOnlySpan<PbtPartition> All => [PbtPartition.Account, PbtPartition.Code, PbtPartition.Storage];

    public static PbtPartition Of(in Stem stem) => OfZone(stem.Zone);

    /// <remarks>Every stored trie-node key starts at or below its partition prefix.</remarks>
    public static PbtPartition Of(in TrieNodeKey key) => OfZone(key.Path.Zone);

    /// <exception cref="NotSupportedException"><paramref name="zone"/> is reserved by EIP-8297.</exception>
    public static PbtPartition OfZone(int zone) => zone switch
    {
        PbtKeyDerivation.AccountZone => PbtPartition.Account,
        PbtKeyDerivation.CodeZone => PbtPartition.Code,
        >= PbtKeyDerivation.FirstStorageZone and < PbtKeyDerivation.ZoneCount => PbtPartition.Storage,
        _ => throw new NotSupportedException($"Zone {zone} is reserved"),
    };

    /// <summary>The fixed-prefix depth at which <paramref name="partition"/> is independently folded.</summary>
    public static int RootDepth(PbtPartition partition) => partition switch
    {
        PbtPartition.Account or PbtPartition.Code => PbtKeyDerivation.ZoneBits,
        PbtPartition.Storage => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(partition)),
    };

    /// <summary>The stored root-group key of <paramref name="partition"/>.</summary>
    public static TrieNodeKey RootKey(PbtPartition partition)
    {
        Span<byte> path = stackalloc byte[Stem.Length];
        path.Clear();
        path[0] = partition switch
        {
            PbtPartition.Account => 0x00,
            PbtPartition.Code => 0x10,
            PbtPartition.Storage => 0x80,
            _ => throw new ArgumentOutOfRangeException(nameof(partition)),
        };
        return TrieNodeKey.For(RootDepth(partition), new Stem(path));
    }

    /// <summary>The number of independently locked stem shards in <paramref name="partition"/>.</summary>
    public static int StemShardCount(PbtPartition partition) => partition switch
    {
        PbtPartition.Account => 16,
        PbtPartition.Code => 1,
        PbtPartition.Storage => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(partition)),
    };

    /// <summary>The partition-local stem shard, after validating that <paramref name="stem"/> belongs to it.</summary>
    public static int StemShard(PbtPartition partition, in Stem stem)
    {
        if (Of(stem) != partition) throw new ArgumentException($"Stem zone {stem.Zone} does not belong to {partition}", nameof(stem));

        return partition switch
        {
            PbtPartition.Account => stem.Bytes[0] & 0x0F,
            PbtPartition.Code => 0,
            PbtPartition.Storage => stem.Bytes[0] >> 3 & 0x0F,
            _ => throw new ArgumentOutOfRangeException(nameof(partition)),
        };
    }
}
