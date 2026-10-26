// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Bounds for sweeping a whole sorted PBT column.</summary>
public static class PbtColumnSweep
{
    /// <summary>An exclusive upper bound past every PBT column key, including the storage keys longer than 32 bytes.</summary>
    public static byte[] PastEveryKey()
    {
        byte[] key = new byte[PbtVariableTreeKey.MaxLength + 1];
        key.AsSpan().Fill(0xFF);
        return key;
    }

    /// <summary>Removes the entries an interrupted import left in an unpublished PBT database.</summary>
    /// <remarks>
    /// An interrupted import can leave logical entries and trie nodes despite a pre-genesis state pointer.
    /// <see cref="TrieUpdater"/> reads a stored root group before its supplied root hash, so stale nodes
    /// would produce the wrong root.
    /// </remarks>
    /// <returns>Whether any entry was removed.</returns>
    public static bool ClearInterruptedImport(IColumnsDb<PbtColumns> pbtDb)
    {
        bool cleared = false;
        pbtDb.GetColumnDb(PbtColumns.Metadata).Remove(PbtRocksDbPersistence.RootNodeGroupKey);

        foreach (PbtColumns column in Enum.GetValues<PbtColumns>())
        {
            if (column == PbtColumns.Metadata) continue;
            IDb columnDb = pbtDb.GetColumnDb(column);
            if (((ISortedKeyValueStore)columnDb).FirstKey is null) continue;
            ((IRangeRemovableKeyValueStore)columnDb).RemoveRange([], PastEveryKey());
            cleared = true;
        }

        return cleared;
    }
}
