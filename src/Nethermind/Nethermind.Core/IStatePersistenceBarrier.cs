// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Core;

/// <summary>
/// Sequences deferred block-data durability against state persistence: the state-persistence path calls
/// <see cref="FlushDeferred"/> before persisting a block, which drains every queued deferred write and then
/// fsyncs the block-data databases.
/// </summary>
/// <remarks>
/// Guarantees <c>state(N) durable =&gt; block-data(&lt;= N) durable</c>: a block's deferred writes are enqueued
/// before its state is persisted, so draining then flushing here makes them durable first. On restart a node
/// re-executes every block whose state was not persisted, so nothing durable lacks its block data. Runs on the
/// caller's state-persistence background thread, off the engine API path.
/// </remarks>
public interface IStatePersistenceBarrier
{
    /// <summary>Whether the barrier is active. When false, all members are no-ops.</summary>
    bool IsEnabled { get; }

    /// <summary>Registers a callback (the deferred writer) that blocks until every queued write is written.</summary>
    void RegisterDrain(Action drain);

    /// <summary>Registers a callback (one per block-data database) that fsyncs that database's write-ahead log.</summary>
    void RegisterFlush(Action flush);

    /// <summary>
    /// Invoked before a block's state is persisted: runs every registered drain, then every registered
    /// flush, so all deferred block data is durable first.
    /// </summary>
    void FlushDeferred();
}

/// <inheritdoc cref="IStatePersistenceBarrier"/>
public sealed class StatePersistenceBarrier(ILogManager? logManager = null) : IStatePersistenceBarrier
{
    // Copy-on-write: rare startup registration rebuilds the arrays under a lock; FlushDeferred reads lock-free.
    private readonly Lock _registrationLock = new();
    private volatile Action[] _drains = [];
    private volatile Action[] _flushes = [];
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<StatePersistenceBarrier>();

    public bool IsEnabled => true;

    public void RegisterDrain(Action drain)
    {
        ArgumentNullException.ThrowIfNull(drain);
        lock (_registrationLock) _drains = Append(_drains, drain);
    }

    public void RegisterFlush(Action flush)
    {
        ArgumentNullException.ThrowIfNull(flush);
        lock (_registrationLock) _flushes = Append(_flushes, flush);
    }

    public void FlushDeferred()
    {
        Action[] drains = _drains;
        for (int i = 0; i < drains.Length; i++) drains[i]();

        Action[] flushes = _flushes;
        for (int i = 0; i < flushes.Length; i++)
        {
            try
            {
                flushes[i]();
            }
            catch (ObjectDisposedException e)
            {
                // On shutdown, a flush target's database can already be disposed by the time the trie store
                // (this barrier's caller, via BarrierNodeStorage) is disposed - the registration is a runtime
                // callback, not a graph edge Autofac's dispose ordering can see. The database already flushed
                // itself on Dispose (see DbOnTheRocks' FlushOnExit), so this is a redundant flush racing
                // shutdown, not a lost write.
                if (_logger.IsDebug) _logger.Debug($"Skipped a block-data flush during shutdown: {e.Message}");
            }
        }
    }

    private static Action[] Append(Action[] existing, Action callback)
    {
        Action[] updated = new Action[existing.Length + 1];
        Array.Copy(existing, updated, existing.Length);
        updated[^1] = callback;
        return updated;
    }
}

/// <summary>No-op barrier for backends or configurations that do not sequence block-data flushing.</summary>
public sealed class NullStatePersistenceBarrier : IStatePersistenceBarrier
{
    public static readonly NullStatePersistenceBarrier Instance = new();

    private NullStatePersistenceBarrier() { }

    public bool IsEnabled => false;

    public void RegisterDrain(Action drain) { }

    public void RegisterFlush(Action flush) { }

    public void FlushDeferred() { }
}
