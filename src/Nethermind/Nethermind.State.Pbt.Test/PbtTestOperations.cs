// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>Builds single-leaf write operations for tests that drive the updater's internals directly.</summary>
internal static class PbtTestOperations
{
    /// <summary>A run holding only <paramref name="key"/>'s leaf, keyed by <paramref name="key"/> verbatim; its run is never returned to a pool.</summary>
    public static PbtWriteOperation<TKey> Leaf<TKey>(TKey key, in ValueHash256 value) where TKey : struct, IPbtKey<TKey> =>
        new(key, SlotRun.Empty.With(SlotRun.IndexOf(key), EvmWordSlot.FromStripped(value.Bytes)));

    public static PbtWriteOperation<TKey> Delete<TKey>(TKey key) where TKey : struct, IPbtKey<TKey> => new(key, SlotRun.Empty);
}
