// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Test;

internal static class SlotRunTestExtensions
{
    /// <summary>Stages a run holding only <paramref name="slotKey"/>; a second slot of the same run in one batch replaces it.</summary>
    public static void SetSlot(this IPbtPersistence.IWriteBatch batch, in PbtStorageTreeKey slotKey, in EvmWord value)
    {
        ISlotRun run = SlotRun.Empty.With(SlotRun.IndexOf(slotKey), value);
        batch.SetSlotRun(SlotRun.RunKey(slotKey), run);
        SlotRun.Return(run);
    }

    /// <summary>Whether the layer holds the slot's run; <paramref name="value"/> is zero for a held but absent slot.</summary>
    public static bool TryGetSlot(this PbtSnapshotContent content, in PbtStorageTreeKey slotKey, out EvmWord value) =>
        content.TryGetSlot(SlotRun.RunKey(slotKey), SlotRun.IndexOf(slotKey), out value);

    public static EvmWord GetSlot(this PbtSnapshotContent content, in PbtStorageTreeKey slotKey)
    {
        content.TryGetSlot(slotKey, out EvmWord value);
        return value;
    }

    /// <summary>Replaces one slot of the run the layer holds, or of the empty run when it holds none.</summary>
    public static void SetSlot(this PbtSnapshotContent content, in PbtStorageTreeKey slotKey, in EvmWord value)
    {
        HashedKey<PbtStorageTreeKey> runKey = SlotRun.RunKey(slotKey);
        ISlotRun current = content.Storages.TryGetValue(runKey, out ISlotRun? held) ? held : SlotRun.Empty;
        content.SetRun(runKey, current.With(SlotRun.IndexOf(slotKey), value));
    }

    /// <summary>The persisted row of a run holding only <paramref name="value"/>, at slot 0.</summary>
    public static byte[] SingleSlotRow(ReadOnlySpan<byte> value) => Bytes.Concat([0x00, 0x01, 0x00], value);
}
