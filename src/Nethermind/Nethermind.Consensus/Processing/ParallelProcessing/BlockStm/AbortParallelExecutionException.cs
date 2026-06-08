// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Signals that the current parallel tx execution must abort because <see cref="MultiVersionMemory.TryRead"/>
/// saw an <c>Estimate</c> from a lower tx still in flight. The exception itself is a control-flow
/// marker; payload (the blocking tx version) is passed on a thread-static so the throw path is
/// allocation-free in the hot loop.
/// </summary>
/// <remarks>
/// We can't return a status from the <see cref="Evm.State.IReadOnlyStateProvider"/> /
/// <c>IWorldStateScopeProvider.IScope</c> read methods without changing those interfaces, so the
/// abort still has to bubble up through the EVM by exception. The expensive parts of `throw new X(...)`
/// — heap allocation of the exception and snapshotting of the stack trace — are eliminated by:
/// <list type="bullet">
/// <item>caching a single instance via <see cref="ExceptionDispatchInfo"/>, which is rethrown
///       without re-capturing the stack; and</item>
/// <item>carrying the blocking <see cref="TxVersion"/> on a <c>[ThreadStatic]</c> field, so the
///       exception object never has to be mutated and no per-throw allocation is needed.</item>
/// </list>
/// The remaining cost is the runtime's frame-unwind on throw, which is unavoidable without an
/// EVM-level abort hook.
/// </remarks>
public sealed class AbortParallelExecutionException : Exception
{
    [ThreadStatic]
    private static TxVersion _blocker;

    private static readonly ExceptionDispatchInfo _dispatch =
        ExceptionDispatchInfo.Capture(new AbortParallelExecutionException());

    private AbortParallelExecutionException() : base(message: null) { }

    /// <summary>
    /// Blocking tx version recorded by the most recent <see cref="Throw"/> on this thread.
    /// Read inside the catch in <c>BlockStmTransactionsExecutor.TryExecute</c>.
    /// </summary>
    public static TxVersion LastBlocker => _blocker;

    /// <summary>
    /// Marker — never returns; control transfers to the nearest catch via the cached
    /// <see cref="ExceptionDispatchInfo"/>. The blocking version is stashed thread-locally.
    /// </summary>
    [DoesNotReturn]
    public static void Throw(in TxVersion blocking)
    {
        _blocker = blocking;
        _dispatch.Throw();
    }

    /// <summary>
    /// Same as <see cref="Throw"/> but typed for switch-expression call sites that need a
    /// value-returning expression (the C# compiler can't infer a switch-expression arm type
    /// from a void-returning method call). Never actually returns.
    /// </summary>
    [DoesNotReturn]
    public static T ThrowAndReturn<T>(in TxVersion blocking)
    {
        _blocker = blocking;
        _dispatch.Throw();
        return default!; // unreachable — _dispatch.Throw() never returns
    }

    // The dispatch is captured before any real throw site, so there is no useful trace. Avoid
    // forcing string materialization if anything ever reads `.StackTrace`.
    public override string? StackTrace => null;
}
