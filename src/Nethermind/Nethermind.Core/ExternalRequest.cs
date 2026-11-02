// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core;

/// <summary>Identifies the external request, such as one JSON-RPC call, whose work the current thread runs.</summary>
/// <remarks>
/// <para>
/// All the work of one request reads one id, however many transactions and blocks it runs: the blocks of an
/// <c>eth_simulateV1</c>, the calls of a <c>trace_callMany</c> or <c>debug_traceCallMany</c>, the runs of an
/// <c>eth_estimateGas</c>. Two requests never share an id, not even within one batch.
/// </para>
/// <para>
/// Thread-static rather than async-local, so entering a request allocates nothing. The JSON-RPC service enters it while
/// the method runs until it returns or first yields, and again while the response is written, where streamed and lazily
/// enumerated results run; an MCP tool call enters one of its own while its body runs until it first yields; the parallel
/// block tracer's workers enter the request of the job they run. Other work handed to another thread, or continued after
/// an await, does not carry the request: it reads whatever its thread has entered then, <see cref="None"/> or another
/// request's id if the continuation runs inline inside that request's scope.
/// </para>
/// </remarks>
public static class ExternalRequest
{
    /// <summary>The id read outside any request.</summary>
    public const long None = 0;

    [ThreadStatic] private static long _current;
    private static long _lastId;

    /// <summary>The id of the request the current thread runs, or <see cref="None"/>.</summary>
    public static long Current => _current;

    /// <summary>A new id, which no other request of this process has.</summary>
    public static long NewId() => Interlocked.Increment(ref _lastId);

    /// <summary>Makes <paramref name="id"/> the current thread's request until the returned scope is disposed.</summary>
    /// <returns>The scope, whose disposal makes the request before it current again.</returns>
    public static Scope Enter(long id)
    {
        Scope scope = new(_current);
        _current = id;
        return scope;
    }

    /// <summary>A request entered with <see cref="Enter"/>, until disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly long _outer;

        internal Scope(long outer) => _outer = outer;

        /// <summary>Makes the request that was current before <see cref="Enter"/> current again.</summary>
        public void Dispose() => _current = _outer;
    }
}
