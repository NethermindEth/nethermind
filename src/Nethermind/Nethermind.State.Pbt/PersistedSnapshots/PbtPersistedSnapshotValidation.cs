// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.PersistedSnapshots;

public static class PbtPersistedSnapshotValidation
{
    public static void Validate(PbtSnapshot source, PbtPersistedSnapshot persisted)
    {
        if (source.From != persisted.From || source.To != persisted.To || source.TreeRoot != persisted.TreeRoot) throw Mismatch();
        PbtSnapshotContent content = source.Content;
        PbtGroupRecord[] groups = [.. content.EnumerateNodeGroups()];
        long count = 0;
        using (PbtPersistedScanner scanner = persisted.Scan())
            while (scanner.MoveNext())
                if (PbtPersistedKey.IsEntity(scanner.Key)) count++;
        if (count != (long)content.Accounts.Count + content.HeaderStorages.Count + content.Storages.Count
            + content.Codes.Count + content.SelfDestructedStorageAddresses.Count + groups.Length) throw Mismatch();
        foreach ((ValueHash256 address, PbtAccount? account) in content.Accounts)
            if (!persisted.TryGetAccount(address, out PbtAccount? actual) || actual != account) throw Mismatch();
        foreach ((ValueHash256 address, _) in content.SelfDestructedStorageAddresses)
            if (!persisted.TryGetStorageClear(address)) throw Mismatch();
        foreach ((ValueHash256 hash, Evm.CodeAnalysis.CodeInfo code) in content.Codes)
            if (!persisted.TryGetCode(hash, out Evm.CodeAnalysis.CodeInfo? actual) || !actual!.CodeSpan.SequenceEqual(code.CodeSpan)) throw Mismatch();
        foreach ((HashedKey<PbtPath> key, PackedSlotRun run) in content.HeaderStorages) ValidateRun(persisted, key.Key, run);
        foreach ((HashedKey<PbtStoragePath> key, PackedSlotRun run) in content.Storages) ValidateRun(persisted, key.Key, run);
        foreach (PbtGroupRecord group in groups) ValidateGroup(persisted, group.Path, group.Payload);
    }

    private static void ValidateRun<TKey>(PbtPersistedSnapshot persisted, TKey key, PackedSlotRun expected) where TKey : struct, IPbtKey<TKey>
    {
        if (!persisted.TryGetSlotRun(key, out PackedSlotRun? actual)) throw Mismatch();
        try
        {
            for (int i = 0; i < 16; i++)
                if (actual!.Get(i) != expected.Get(i)) throw Mismatch();
        }
        finally { SlotRun.Return(actual!); }
    }

    private static void ValidateGroup(PbtPersistedSnapshot persisted, PbtStorageNodePath key, ReadOnlyMemory<byte> expected)
    {
        if (!persisted.TryLoadNodeGroup(key, static (actual, expected) => { if (!actual.SequenceEqual(expected)) throw Mismatch(); }, expected.Span, out bool tombstone)
            || tombstone) throw Mismatch();
    }

    private static InvalidDataException Mismatch() => new("Converted persisted PBT snapshot differs from its source.");
}
