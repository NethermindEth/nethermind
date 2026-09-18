// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Nethermind.Core.Threading;

public static partial class Rayon
{
    /// <summary>A unit of work that a thief or the injector loop can run to completion.</summary>
    internal abstract class Job
    {
        public abstract void Execute();
    }

    /// <summary>
    /// Completion flag of a job. A job pushed by a worker wakes that worker when set; an injected job
    /// (no owner) is waited on through an event by the non-worker thread that injected it.
    /// </summary>
    internal sealed class Latch(Worker? owner)
    {
        private int _state;
        private readonly ManualResetEventSlim? _event = owner is null ? new(false) : null;

        public bool IsSet => Volatile.Read(ref _state) != 0;

        public void Set()
        {
            // Full fence: publishes the job's result before the waiter can observe the flag.
            Interlocked.Exchange(ref _state, 1);
            if (owner is not null)
            {
                owner.TryWake();
            }
            else
            {
                _event!.Set();
            }
        }

        public void WaitCold() => _event!.Wait();
    }

    /// <summary>
    /// The second operand of a <see cref="Join{TSa,TSb,TA,TB}"/>. Lives on the heap because a thief on
    /// another thread may run it after the pushing frame has moved on; the state is copied in.
    /// </summary>
    internal sealed class StackJob<TState, TResult>(in TState state, Func<TState, bool, TResult> func, Worker? owner) : Job
    {
        private readonly TState _state = state;
        private readonly Func<TState, bool, TResult> _func = func;
        private TResult? _result;
        private ExceptionDispatchInfo? _exception;

        public readonly Latch Latch = new(owner);

        /// <summary>Runs on a thief (or the owner on the recovery path); the result is handed back through the latch.</summary>
        public override void Execute()
        {
            try
            {
                _result = _func(_state, true);
            }
            catch (Exception e)
            {
                _exception = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                Latch.Set();
            }
        }

        /// <summary>Runs on the owner after it popped the job back; exceptions propagate directly.</summary>
        public TResult RunInline() => _func(_state, false);

        public TResult IntoResult()
        {
            _exception?.Throw();
            return _result!;
        }
    }
}
