// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.PersistedSnapshots;

public static class PbtPersistedSnapshotValidation
{
    public static void Validate(PbtSnapshot source, PbtPersistedSnapshot persisted)
    {
        if (source.From != persisted.From || source.To != persisted.To || source.TreeRoot != persisted.TreeRoot) throw Mismatch();
        PbtSnapshotContent content = source.Content;
        long count = 0;
        using (PbtPersistedScanner scanner = persisted.Scan())
            while (scanner.MoveNext())
                if (PbtPersistedKey.IsEntity(scanner.Key)) count++;
        if (count != (long)content.Accounts.Count + content.HeaderStorages.Count + content.Storages.Count
            + content.Codes.Count + content.SelfDestructedStorageAddresses.Count + content.AccountNodeGroups.Count
            + content.CodeNodeGroups.Count + content.StorageNodeGroups.Count) throw Mismatch();
        foreach ((ValueHash256 address, PbtAccount? account) in content.Accounts)
            if (!persisted.TryGetAccount(address, out PbtAccount? actual) || actual != account) throw Mismatch();
        foreach ((ValueHash256 address, _) in content.SelfDestructedStorageAddresses)
            if (!persisted.TryGetStorageClear(address)) throw Mismatch();
        foreach ((ValueHash256 hash, Evm.CodeAnalysis.CodeInfo code) in content.Codes)
            if (!persisted.TryGetCode(hash, out Evm.CodeAnalysis.CodeInfo? actual) || !actual!.CodeSpan.SequenceEqual(code.CodeSpan)) throw Mismatch();
        foreach ((HashedKey<PbtPath> key, PackedSlotRun run) in content.HeaderStorages) ValidateRun(persisted, key.Key, run);
        foreach ((HashedKey<PbtStoragePath> key, PackedSlotRun run) in content.Storages) ValidateRun(persisted, key.Key, run);
        foreach ((PbtNodePath key, RefCountingMemory? group) in content.AccountNodeGroups) ValidateGroup(persisted, key, group);
        foreach ((PbtNodePath key, RefCountingMemory? group) in content.CodeNodeGroups) ValidateGroup(persisted, key, group);
        foreach ((PbtStorageNodePath key, RefCountingMemory? group) in content.StorageNodeGroups) ValidateGroup(persisted, key, group);
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

    private static void ValidateGroup<TPath>(PbtPersistedSnapshot persisted, TPath key, RefCountingMemory? expected) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!persisted.TryGetNodeGroup(key, out RefCountingMemory? actual)) throw Mismatch();
        using (actual)
            if (expected is null ? actual is not null : actual is null || !expected.GetSpan().SequenceEqual(actual.GetSpan())) throw Mismatch();
    }

    private static InvalidDataException Mismatch() => new("Converted persisted PBT snapshot differs from its source.");
}
