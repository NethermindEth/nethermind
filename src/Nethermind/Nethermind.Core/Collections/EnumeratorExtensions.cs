// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Nethermind.Core.Collections;

public static class EnumeratorExtensions
{
    /// <summary>Splits <paramref name="source"/> into two enumerators that each yield every item in order.</summary>
    /// <remarks>
    /// Each branch may be consumed on its own thread. Whichever branch is ahead reads the source and buffers the item
    /// for the other, blocking once the other is <paramref name="capacity"/> items behind. A source failure is rethrown by
    /// each branch once it has drained what was read before it. Disposing a branch stops buffering for it, so the other is
    /// never held back by it; the source is disposed once both branches are.
    /// </remarks>
    public static (IEnumerator<T> First, IEnumerator<T> Second) Teed<T>(this IEnumerator<T> source, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        TeeSource<T> shared = new(source, capacity);
        return (new TeeBranch<T>(shared, 0), new TeeBranch<T>(shared, 1));
    }

    private sealed class TeeSource<T>(IEnumerator<T> source, int capacity)
    {
        private readonly object _gate = new();
        private readonly Queue<T>[] _pending = [new(), new()];
        private readonly bool[] _released = new bool[2];
        private int _waiting;
        private bool _reading;
        private bool _ended;
        private ExceptionDispatchInfo? _failure;

        public bool TryTake(int branch, out T item)
        {
            int other = 1 - branch;
            lock (_gate)
            {
                while (true)
                {
                    if (_pending[branch].TryDequeue(out item!))
                    {
                        WakeWaiters();
                        return true;
                    }
                    _failure?.Throw();
                    if (_ended) return false;
                    if (!_reading && (_released[other] || _pending[other].Count < capacity)) break;
                    _waiting++;
                    Monitor.Wait(_gate);
                    _waiting--;
                }
                _reading = true;
            }

            bool moved;
            try
            {
                moved = source.MoveNext();
                item = moved ? source.Current : default!;
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _failure = ExceptionDispatchInfo.Capture(exception);
                    _reading = false;
                    WakeWaiters();
                }
                throw;
            }

            lock (_gate)
            {
                _reading = false;
                if (!moved) _ended = true;
                else if (!_released[other]) _pending[other].Enqueue(item);
                WakeWaiters();
            }
            return moved;
        }

        public void Release(int branch)
        {
            lock (_gate)
            {
                if (_released[branch]) return;
                _released[branch] = true;
                _pending[branch].Clear();
                WakeWaiters();
                if (!_released[1 - branch]) return;
            }
            source.Dispose();
        }

        private void WakeWaiters()
        {
            if (_waiting != 0) Monitor.PulseAll(_gate);
        }
    }

    private sealed class TeeBranch<T>(TeeSource<T> shared, int branch) : IEnumerator<T>
    {
        public T Current { get; private set; } = default!;

        object? IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (!shared.TryTake(branch, out T item)) return false;
            Current = item;
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose() => shared.Release(branch);
    }
}
