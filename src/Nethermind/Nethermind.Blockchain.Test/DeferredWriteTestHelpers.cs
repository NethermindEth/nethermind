// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Test;

internal static class DeferredWriteTestHelpers
{
    private const int Capacity = 8;

    /// <summary>
    /// A writer with no background consumer, so <c>Pump()</c> / the barrier drain make pre-flush states
    /// deterministic. Registers its drain with <paramref name="barrier"/> when one is given.
    /// </summary>
    public static DeferredBlockDataWriter ManualWriter(IStatePersistenceBarrier? barrier = null) =>
        new(enabled: true, capacity: Capacity, LimboLogs.Instance, barrier, startConsumer: false);

    /// <summary>A disabled writer: <c>Enqueue</c> runs work inline, so inserts are synchronous.</summary>
    public static DeferredBlockDataWriter DisabledWriter() =>
        new(enabled: false, capacity: Capacity, LimboLogs.Instance, persistenceBarrier: null, startConsumer: false);
}
