// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>Every leaf a test tree holds, so a partial batch of leaf writes can be completed into the whole runs a write batch takes.</summary>
internal sealed class PbtLeafModel
{
    private readonly Dictionary<byte[], byte[]> _leaves = new(Bytes.EqualityComparer);

    /// <summary>Applies <paramref name="writes"/> without preparing runs, for a tree that already holds them.</summary>
    public void Apply(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        foreach ((_, ISlotRun run) in Complete(writes)) SlotRun.Return(run);
    }

    /// <summary>Applies <paramref name="writes"/> (a null or zero value deletes) and yields the whole run of every run they touch; the caller returns each run.</summary>
    public IEnumerable<(byte[] RunKey, ISlotRun Run)> Complete(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        HashSet<byte[]> touched = new(Bytes.EqualityComparer);
        foreach ((byte[] key, byte[]? value) in writes)
        {
            if (value is null || value.AsSpan().IndexOfAnyExcept((byte)0) < 0) _leaves.Remove(key);
            else _leaves[key] = value;
            byte[] runKey = (byte[])key.Clone();
            runKey[^1] &= 0xF0;
            touched.Add(runKey);
        }
        foreach (byte[] runKey in touched)
        {
            ISlotRun run = SlotRun.Empty;
            for (int index = 0; index < SlotRun.Width; index++)
            {
                byte[] key = (byte[])runKey.Clone();
                key[^1] |= (byte)index;
                if (!_leaves.TryGetValue(key, out byte[]? value)) continue;
                ISlotRun previous = run;
                run = run.With(index, EvmWordSlot.FromStripped(value));
                SlotRun.Return(previous);
            }
            yield return (runKey, run);
        }
    }
}
