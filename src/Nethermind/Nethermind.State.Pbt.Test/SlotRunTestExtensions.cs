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
    public static void SetSlot<TKey>(this IPbtPersistence.IWriteBatch batch, in TKey slotKey, in EvmWord value) where TKey : struct, IPbtKey<TKey>
    {
        PackedSlotRun run = SlotRun.Empty.With(SlotRun.IndexOf(slotKey), value);
        batch.SetSlotRun(SlotRun.RunKey(slotKey), run);
        SlotRun.Return(run);
    }

    public static EvmWord GetSlot<TKey>(this IPbtPersistence.IReader reader, in TKey slotKey) where TKey : struct, IPbtKey<TKey>
    {
        PackedSlotRun run = reader.GetSlotRun(SlotRun.RunKey(slotKey));
        EvmWord value = run.Get(SlotRun.IndexOf(slotKey));
        SlotRun.Return(run);
        return value;
    }

    /// <summary>Whether the layer holds the slot's run; <paramref name="value"/> is zero for a held but absent slot.</summary>
    public static bool TryGetSlot<TKey>(this PbtSnapshotContent content, in TKey slotKey, out EvmWord value) where TKey : struct, IPbtKey<TKey>
    {
        bool held = content.TryGetSlotRun<TKey>(SlotRun.RunKey(slotKey), out PackedSlotRun? run);
        value = run?.Get(SlotRun.IndexOf(slotKey)) ?? default;
        return held;
    }

    public static EvmWord GetSlot<TKey>(this PbtSnapshotContent content, in TKey slotKey) where TKey : struct, IPbtKey<TKey>
    {
        content.TryGetSlot(slotKey, out EvmWord value);
        return value;
    }

    /// <summary>Replaces one slot of the run the layer holds, or of the empty run when it holds none.</summary>
    public static void SetSlot<TKey>(this PbtSnapshotContent content, in TKey slotKey, in EvmWord value) where TKey : struct, IPbtKey<TKey>
    {
        HashedKey<TKey> runKey = SlotRun.RunKey(slotKey);
        PackedSlotRun current = content.TryGetSlotRun(runKey, out PackedSlotRun? held) ? held : SlotRun.Empty;
        content.SetRun(runKey, current.With(SlotRun.IndexOf(slotKey), value));
    }

    /// <summary>Stages a run holding only <paramref name="slotKey"/>, of whichever zone its slot lies in.</summary>
    public static void SetSlot(this IPbtPersistence.IWriteBatch batch, in PbtVariableTreeKey slotKey, in EvmWord value)
    {
        if (IsHeaderSlot(slotKey)) batch.SetSlot((PbtPath)slotKey, value);
        else batch.SetSlot((PbtStoragePath)slotKey, value);
    }

    public static EvmWord GetSlot(this IPbtPersistence.IReader reader, in PbtVariableTreeKey slotKey) =>
        IsHeaderSlot(slotKey) ? reader.GetSlot((PbtPath)slotKey) : reader.GetSlot((PbtStoragePath)slotKey);

    public static bool TryGetSlot(this PbtSnapshotContent content, in PbtVariableTreeKey slotKey, out EvmWord value) =>
        IsHeaderSlot(slotKey) ? content.TryGetSlot((PbtPath)slotKey, out value) : content.TryGetSlot((PbtStoragePath)slotKey, out value);

    public static EvmWord GetSlot(this PbtSnapshotContent content, in PbtVariableTreeKey slotKey)
    {
        content.TryGetSlot(slotKey, out EvmWord value);
        return value;
    }

    public static void SetSlot(this PbtSnapshotContent content, in PbtVariableTreeKey slotKey, in EvmWord value)
    {
        if (IsHeaderSlot(slotKey)) content.SetSlot((PbtPath)slotKey, value);
        else content.SetSlot((PbtStoragePath)slotKey, value);
    }

    /// <summary>The run the layer holds for the slot of <paramref name="slotKey"/>.</summary>
    public static PackedSlotRun GetRun(this PbtSnapshotContent content, in PbtVariableTreeKey slotKey) => IsHeaderSlot(slotKey)
        ? content.HeaderStorages[SlotRun.RunKey((PbtPath)slotKey)]
        : content.Storages[SlotRun.RunKey((PbtStoragePath)slotKey)];

    private static bool IsHeaderSlot(in PbtVariableTreeKey slotKey) => slotKey.Length == PbtPath.KeyLength;

    /// <summary>The persisted row of a run holding only <paramref name="value"/>, at slot 0.</summary>
    public static byte[] SingleSlotRow(ReadOnlySpan<byte> value) => Bytes.Concat([0x00, 0x01, 0x00], value);
}
