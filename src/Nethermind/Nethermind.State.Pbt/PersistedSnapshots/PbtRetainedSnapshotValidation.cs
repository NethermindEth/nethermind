// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.PersistedSnapshots;

internal static class PbtRetainedSnapshotValidation
{
    internal static void Validate(PbtSnapshot source, PbtRetainedSnapshot retained)
    {
        if (source.From != retained.From || source.To != retained.To || source.TreeRoot != retained.TreeRoot) throw Mismatch();
        PbtSnapshotContent content = source.Content;
        long count = 0;
        using (PbtRetainedScanner scanner = retained.Scan())
            while (scanner.MoveNext())
                if (PbtRetainedKey.IsEntity(scanner.Key)) count++;
        if (count != (long)content.Accounts.Count + content.HeaderStorages.Count + content.Storages.Count
            + content.Codes.Count + content.SelfDestructedStorageAddresses.Count + content.AccountNodeGroups.Count
            + content.CodeNodeGroups.Count + content.StorageNodeGroups.Count) throw Mismatch();
        foreach ((ValueHash256 address, PbtAccount? account) in content.Accounts)
            if (!retained.TryGetAccount(address, out PbtAccount? actual) || actual != account) throw Mismatch();
        foreach ((ValueHash256 address, bool value) in content.SelfDestructedStorageAddresses)
            if (!retained.TryGetStorageClear(address, out bool actual) || actual != value) throw Mismatch();
        foreach ((ValueHash256 hash, Evm.CodeAnalysis.CodeInfo code) in content.Codes)
            if (!retained.TryGetCode(hash, out Evm.CodeAnalysis.CodeInfo? actual) || !actual!.CodeSpan.SequenceEqual(code.CodeSpan)) throw Mismatch();
        foreach ((HashedKey<PbtPath> key, PackedSlotRun run) in content.HeaderStorages) ValidateRun(retained, key.Key, run);
        foreach ((HashedKey<PbtStoragePath> key, PackedSlotRun run) in content.Storages) ValidateRun(retained, key.Key, run);
        foreach ((PbtNodePath key, RefCountingMemory? group) in content.AccountNodeGroups) ValidateGroup(retained, key, group);
        foreach ((PbtNodePath key, RefCountingMemory? group) in content.CodeNodeGroups) ValidateGroup(retained, key, group);
        foreach ((PbtStorageNodePath key, RefCountingMemory? group) in content.StorageNodeGroups) ValidateGroup(retained, key, group);
    }

    private static void ValidateRun<TKey>(PbtRetainedSnapshot retained, TKey key, PackedSlotRun expected) where TKey : struct, IPbtKey<TKey>
    {
        if (!retained.TryGetSlotRun(key, out PackedSlotRun? actual)) throw Mismatch();
        try
        {
            for (int i = 0; i < 16; i++)
                if (actual!.Get(i) != expected.Get(i)) throw Mismatch();
        }
        finally { SlotRun.Return(actual!); }
    }

    private static void ValidateGroup<TPath>(PbtRetainedSnapshot retained, TPath key, RefCountingMemory? expected) where TPath : struct, IPbtNodePath<TPath>
    {
        if (!retained.TryGetNodeGroup(key, out RefCountingMemory? actual)) throw Mismatch();
        using (actual)
            if (expected is null ? actual is not null : actual is null || !expected.GetSpan().SequenceEqual(actual.GetSpan())) throw Mismatch();
    }

    private static InvalidDataException Mismatch() => new("Converted retained PBT snapshot differs from its source.");
}
