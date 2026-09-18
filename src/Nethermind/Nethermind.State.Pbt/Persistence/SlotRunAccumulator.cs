// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Collects slot writes into whole runs for staging writers that see one slot at a time.</summary>
/// <remarks>Flush before moving to slots whose runs were flushed already: a run written twice is replaced, not merged.</remarks>
internal sealed class SlotRunAccumulator
{
    private readonly Dictionary<PbtStorageTreeKey, ISlotRun> _runs = [];

    public void Add(in PbtStorageTreeKey slotKey, in EvmWord value)
    {
        PbtStorageTreeKey runKey = SlotRun.RunKey(slotKey);
        ISlotRun previous = _runs.TryGetValue(runKey, out ISlotRun? held) ? held : SlotRun.Empty;
        _runs[runKey] = previous.With(SlotRun.IndexOf(slotKey), value);
        SlotRun.Return(previous);
    }

    public void FlushTo(Func<IPbtPersistence.IWriteBatch> batch)
    {
        foreach ((PbtStorageTreeKey runKey, ISlotRun run) in _runs)
        {
            batch().SetSlotRun(runKey, run);
            SlotRun.Return(run);
        }
        _runs.Clear();
    }
}
