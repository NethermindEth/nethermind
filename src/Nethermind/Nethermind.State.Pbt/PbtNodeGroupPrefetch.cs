// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Core.Threading;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>Reads ahead the node groups the fold of a block access list walks into, keeping them in the bundle for the fold.</summary>
internal static class PbtNodeGroupPrefetch
{
    private const int AccountGroupDepth = PbtRocksDbPersistence.AccountTopDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
    private const int StorageGroupDepth = PbtRocksDbPersistence.StemTopDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
    private const long AverageNodeGroupBytes = 1024;

    /// <summary>Reads the first groups below the top node groups that the fold of <paramref name="bal"/> walks into.</summary>
    /// <remarks>
    /// Each storage leaf then also prefetches the groups below its first storage group, as deep as its estimated remaining group levels.
    /// A group the fold has already read is skipped.
    /// The bundle keeps the persisted groups read for the fold until its write buffer is sealed.
    /// </remarks>
    internal static void Prefetch(PbtSnapshotBundle bundle, ReadOnlyBlockAccessList bal, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested) return;
        PbtStorageNodePath[] groups = [.. FirstGroupPaths(bal)];
        long[] descendantBytes = new long[groups.Length * PbtFourLevelGroupGeometry.BoundarySlots];
        RefCountingMemory?[] snapshots = new RefCountingMemory?[groups.Length];
        RefCountingMemory?[] persisted = [];
        try
        {
            List<PbtStorageNodePath> misses = [];
            List<int> missingIndexes = [];
            for (int index = 0; index < groups.Length; index++)
            {
                if (cancellation.IsCancellationRequested) return;
                if (!bundle.ShouldPrefetchNodeGroup(groups[index])) continue;
                if (!bundle.TryGetSnapshotNodeGroup(groups[index], out snapshots[index]))
                {
                    misses.Add(groups[index]);
                    missingIndexes.Add(index);
                }
            }
            PbtStorageNodePath[] missingPaths = [.. misses];
            if (cancellation.IsCancellationRequested) return;
            persisted = new RefCountingMemory?[missingPaths.Length];
            ParallelUnbalancedWork.For(0, missingPaths.Length, (bundle, missingPaths, persisted, cancellation), static (index, state) =>
            {
                if (!state.cancellation.IsCancellationRequested) state.persisted[index] = state.bundle.GetPersistedNodeGroup(state.missingPaths[index]);
                return state;
            });
            if (cancellation.IsCancellationRequested) return;

            for (int index = 0; index < groups.Length; index++)
                if (groups[index].BitDepth == StorageGroupDepth)
                    StorageDescendantBytes(snapshots[index], descendantBytes.AsSpan(index * PbtFourLevelGroupGeometry.BoundarySlots, PbtFourLevelGroupGeometry.BoundarySlots));
            for (int index = 0; index < missingPaths.Length; index++)
                if (missingPaths[index].BitDepth == StorageGroupDepth)
                    StorageDescendantBytes(persisted[index], descendantBytes.AsSpan(missingIndexes[index] * PbtFourLevelGroupGeometry.BoundarySlots, PbtFourLevelGroupGeometry.BoundarySlots));
        }
        finally
        {
            foreach (RefCountingMemory? payload in snapshots) ((IDisposable?)payload)?.Dispose();
            foreach (RefCountingMemory? payload in persisted) ((IDisposable?)payload)?.Dispose();
        }
        if (cancellation.IsCancellationRequested) return;

        foreach (PbtStorageNodePath path in DeeperStorageGroupPaths(bal, groups, descendantBytes))
        {
            if (cancellation.IsCancellationRequested) return;
            if (!bundle.ShouldPrefetchNodeGroup(path)) continue;
            if (!bundle.TryGetSnapshotNodeGroup(path, out RefCountingMemory? snapshot)) ((IDisposable?)bundle.GetPersistedNodeGroup(path))?.Dispose();
            ((IDisposable?)snapshot)?.Dispose();
        }
    }

    private static void StorageDescendantBytes(RefCountingMemory? payload, Span<long> descendantBytes)
    {
        if (payload is null) return;

        ReadOnlySpan<byte> bytes = payload.GetSpan();
        ushort mask = PbtNodeGroupCodec.ReadDescendantMask(bytes);
        for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
            if ((mask & (1 << slot)) != 0) descendantBytes[slot] = PbtNodeGroupCodec.ReadDescendantBytes(bytes, mask, slot);
    }

    /// <summary>The distinct groups below the first storage groups along the storage leaves <paramref name="bal"/> writes.</summary>
    /// <remarks>
    /// A leaf's boundary slot in its first storage group holds about <c>descendantBytes / 1 KiB</c> groups,
    /// so its remaining depth is about <c>log16</c> of that many group levels.
    /// </remarks>
    private static HashSet<PbtStorageNodePath> DeeperStorageGroupPaths(ReadOnlyBlockAccessList bal, PbtStorageNodePath[] groups, long[] descendantBytes)
    {
        Dictionary<PbtStorageNodePath, int> groupIndexes = [];
        for (int index = 0; index < groups.Length; index++) groupIndexes[groups[index]] = index;

        HashSet<PbtStorageNodePath> paths = [];
        Span<byte> pathBytes = stackalloc byte[PbtStoragePath.KeyLength];
        foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
        {
            ValueHash256 addressHash = PbtStateKey.AddressKeyHash(accountChanges.Address);
            foreach (ReadOnlySlotChanges slotChanges in accountChanges.StorageChanges)
            {
                if (slotChanges.Changes.Length == 0 || PbtStateKey.IsHeaderSlot(slotChanges.Key)) continue;
                PbtStoragePath storagePath = PbtStateKey.Storage(accountChanges.Address, addressHash, slotChanges.Key);
                int groupIndex = groupIndexes[new PbtStorageNodePath(storagePath.Bytes[..(StorageGroupDepth / 8)], StorageGroupDepth)];
                int slot = storagePath.Bytes[StorageGroupDepth / 8] >> 4;
                long subtreeBytes = descendantBytes[groupIndex * PbtFourLevelGroupGeometry.BoundarySlots + slot];
                int levels = subtreeBytes <= AverageNodeGroupBytes ? 0 : (int)Math.Ceiling(Math.Log((double)subtreeBytes / AverageNodeGroupBytes, 16));
                for (int level = 1; level <= levels; level++)
                {
                    int depth = StorageGroupDepth + level * PbtFourLevelGroupGeometry.LevelsPerGroup;
                    Span<byte> path = pathBytes[..((depth + 7) / 8)];
                    storagePath.Bytes[..path.Length].CopyTo(path);
                    if (depth % 8 != 0) path[^1] &= 0xF0;
                    paths.Add(new PbtStorageNodePath(path, depth));
                }
            }
        }
        return paths;
    }

    /// <summary>The distinct paths of the first groups below the top node groups that the leaves <paramref name="bal"/> writes lie under.</summary>
    /// <remarks>Code leaves are left out: they are content addressed, so a deployment rarely finds their groups stored.</remarks>
    internal static HashSet<PbtStorageNodePath> FirstGroupPaths(ReadOnlyBlockAccessList bal)
    {
        HashSet<PbtStorageNodePath> paths = [];
        Span<byte> pathBytes = stackalloc byte[StorageGroupDepth / 8];
        foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
        {
            bool writesAccountZone = accountChanges.HasBalanceNonceOrCodeChanges;
            bool writesStorageZone = false;
            foreach (ReadOnlySlotChanges slotChanges in accountChanges.StorageChanges)
            {
                if (slotChanges.Changes.Length == 0) continue;
                if (PbtStateKey.IsHeaderSlot(slotChanges.Key)) writesAccountZone = true;
                else writesStorageZone = true;
            }
            if (!writesAccountZone && !writesStorageZone) continue;

            PbtStateKey.AddressKeyHash(accountChanges.Address).Bytes.CopyTo(pathBytes[1..]);
            if (writesAccountZone)
            {
                pathBytes[0] = Eip8297KeyDerivation.AccountZone;
                paths.Add(new PbtStorageNodePath(pathBytes[..(AccountGroupDepth / 8)], AccountGroupDepth));
            }
            if (writesStorageZone)
            {
                pathBytes[0] = Eip8297KeyDerivation.StorageZone;
                paths.Add(new PbtStorageNodePath(pathBytes, StorageGroupDepth));
            }
        }
        return paths;
    }
}
