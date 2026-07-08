// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core;

/// <summary>
/// Sequences deferred block-data durability against state persistence. Block-data stores (receipts,
/// bodies) register a flush callback; the state-persistence path calls <see cref="FlushBefore"/> with
/// the block number whose state is about to be persisted, forcing every registered store to make its
/// data for that block (and all earlier) durable first.
/// </summary>
/// <remarks>
/// Guarantees <c>state(N) durable =&gt; block-data(&lt;= N) durable</c>. A node re-executes on restart every
/// block whose state is not persisted, so nothing durable is ever left without its block data.
/// <para>
/// An ordering gate, not the primary drain: a bounded background writer keeps the overlay shallow, so
/// <see cref="FlushBefore"/> normally just fsyncs the WAL of writes the writer already made. Callbacks
/// run synchronously on the caller's state-persistence background thread, off the engine API path.
/// </para>
/// <para>
/// A callback that throws aborts the persist before state is written, preserving the invariant. Hard
/// write/flush failures are handled by the database layer as for state's own WAL flush, so the
/// abort-on-throw guarantee covers propagated errors, not every possible flush failure.
/// </para>
/// </remarks>
public interface IStatePersistenceBarrier
{
    /// <summary>Whether the barrier is active. When false, <see cref="Register"/> and <see cref="FlushBefore"/> are no-ops.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Registers a callback that must, for every entry the store holds with block number less than or
    /// equal to its argument, write it to the store's database and flush that database's write-ahead
    /// log before returning.
    /// </summary>
    /// <param name="flushUpToInclusive">The callback, invoked with the highest block number to flush.</param>
    void Register(Action<long> flushUpToInclusive);

    /// <summary>
    /// Invoked by the state-persistence path immediately before the state for <paramref name="blockNumber"/>
    /// is written, driving every registered store to make its data for that block (and earlier) durable.
    /// </summary>
    /// <param name="blockNumber">The block number whose state is about to be persisted.</param>
    void FlushBefore(long blockNumber);
}

/// <inheritdoc cref="IStatePersistenceBarrier"/>
public sealed class StatePersistenceBarrier : IStatePersistenceBarrier
{
    // Copy-on-write: registration happens a handful of times at startup, FlushBefore runs on every
    // state persist, so readers take a lock-free snapshot and writers rebuild the array under a lock.
    private readonly Lock _registrationLock = new();
    private volatile Action<long>[] _hooks = [];

    public bool IsEnabled => true;

    public void Register(Action<long> flushUpToInclusive)
    {
        ArgumentNullException.ThrowIfNull(flushUpToInclusive);
        lock (_registrationLock)
        {
            Action<long>[] updated = new Action<long>[_hooks.Length + 1];
            Array.Copy(_hooks, updated, _hooks.Length);
            updated[^1] = flushUpToInclusive;
            _hooks = updated;
        }
    }

    public void FlushBefore(long blockNumber)
    {
        Action<long>[] hooks = _hooks;
        for (int i = 0; i < hooks.Length; i++)
        {
            hooks[i](blockNumber);
        }
    }
}

/// <summary>No-op barrier for backends or configurations that do not sequence block-data flushing.</summary>
public sealed class NullStatePersistenceBarrier : IStatePersistenceBarrier
{
    public static readonly NullStatePersistenceBarrier Instance = new();

    private NullStatePersistenceBarrier() { }

    public bool IsEnabled => false;

    public void Register(Action<long> flushUpToInclusive) { }

    public void FlushBefore(long blockNumber) { }
}
