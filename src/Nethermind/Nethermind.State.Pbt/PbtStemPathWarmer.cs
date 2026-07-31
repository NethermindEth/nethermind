// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.Tiles;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.State.Pbt;

/// <summary>Loads the hash-qualified PBT groups and prior leaf consumed when a stem is folded.</summary>
internal static class PbtStemPathWarmer
{
    public static void Warm(PbtSnapshotBundle bundle, PbtTrieLayout layout, PbtPartitionRoots roots, in Stem stem)
    {
        PbtPartition partition = PbtPartitions.Of(stem);
        PbtPartitionRoot root = roots[partition];
        if (root.Kind == NodeKind.Absent) return;

        switch (layout.Tiling())
        {
            case PbtTiling.SixLevel:
                WarmPartition<PbtSixLevelTileLayout>(bundle, partition, root, stem);
                break;
            case PbtTiling.EightLevel:
                WarmPartition<PbtEightLevelTileLayout>(bundle, partition, root, stem);
                break;
            case PbtTiling.FourLevel:
                WarmPartition<PbtFourLevelTileLayout>(bundle, partition, root, stem);
                break;
            case PbtTiling.FiveLevel:
                WarmPartition<PbtFiveLevelTileLayout>(bundle, partition, root, stem);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }

    private static void WarmPartition<TLayout>(
        PbtSnapshotBundle bundle, PbtPartition partition, in PbtPartitionRoot root, in Stem stem)
        where TLayout : IPbtTileLayout
    {
        if (partition == PbtPartition.Storage)
            WarmStoredGroup<PbtRootedTileLayout<TLayout, PbtDepth1>>(bundle, PbtPartitions.RootKey(partition), root.Hash, stem);
        else
            WarmStoredGroup<PbtRootedTileLayout<TLayout, PbtDepth4>>(bundle, PbtPartitions.RootKey(partition), root.Hash, stem);
    }

    private static void WarmStoredGroup<TLayout>(
        PbtSnapshotBundle bundle, in TrieNodeKey key, in ValueHash256 hash, in Stem stem)
        where TLayout : IPbtTileLayout
    {
        using RefCountingMemory? memory = bundle.GetTrieNode(key, hash);
        if (memory is null) return;

        WarmGroup<TLayout>(bundle, key, TreeReader<TLayout>.Of(memory), stem);
    }

    private static void WarmGroup<TLayout>(
        PbtSnapshotBundle bundle, in TrieNodeKey key, in TreeReader<TLayout> stored, in Stem stem)
        where TLayout : IPbtTileLayout
    {
        TreeReader<TLayout> occupants = stored.AsGroup();
        PbtTrieNodeGroup<TLayout> group = occupants.Group();
        int slot = TLayout.SlotOf(stem, key.Depth);
        TreeReader<TLayout> reader = occupants.Reader(slot, group);
        Occupant occupant = reader.Occupant;

        switch (occupant.Kind)
        {
            case NodeKind.Absent:
                return;
            case NodeKind.Stem:
                if (occupant.Stem == stem)
                {
                    using RefCountingMemory? leaf = bundle.GetLeafBlob(stem, occupant.Hash);
                }
                return;
            case NodeKind.Chain:
                WarmChain<TLayout>(bundle, key, reader, stem);
                return;
            case NodeKind.Internal:
                int childDepth = key.Depth + TLayout.LevelsPerGroup;
                TrieNodeKey childKey = TrieNodeKey.For(childDepth, stem);
                if (occupants.HasChild(slot, group))
                    WarmGroup<TLayout>(bundle, childKey, occupants.Child(slot, group), stem);
                else
                    WarmStoredGroup<TLayout>(bundle, childKey, occupant.NodeHash(), stem);
                return;
            default:
                throw new InvalidDataException($"Unexpected PBT node kind {occupant.Kind}");
        }
    }

    private static void WarmChain<TLayout>(
        PbtSnapshotBundle bundle, in TrieNodeKey key, in TreeReader<TLayout> reader, in Stem stem)
        where TLayout : IPbtTileLayout
    {
        PbtNodeChain chain = PbtNodeChain.Decode<TLayout>(reader.Data, key.Depth);
        using RefCountingMemory? target = bundle.GetTrieNode(chain.TargetKey, chain.TargetHash);
        int differingBit = stem.FirstDifferingBit(chain.TargetPath, key.Depth);
        if (target is null || (uint)differingBit < (uint)chain.TargetDepth) return;

        WarmGroup<TLayout>(bundle, chain.TargetKey, TreeReader<TLayout>.Of(target), stem);
    }
}
