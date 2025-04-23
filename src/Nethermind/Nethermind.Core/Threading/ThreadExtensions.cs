// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;

public static class ThreadExtensions
{
    public readonly struct Disposable : IDisposable
    {
        private readonly Thread? _thread;
        private readonly ThreadPriority _previousPriority;

        internal Disposable(Thread thread, ThreadPriority priority = ThreadPriority.AboveNormal)
        {
            _thread = thread;
            _previousPriority = thread.Priority;
            thread.Priority = priority;
        }

        public void Dispose()
        {
            if (_thread is not null && Thread.CurrentThread == _thread)
            {
                _thread.Priority = _previousPriority;
            }
        }
    }

    public static Disposable BoostPriority(this Thread thread)
    {
        return new Disposable(thread);
    }

    public static Disposable BoostPriorityHighest(this Thread thread)
    {
        return new Disposable(thread, ThreadPriority.Highest);
    }

    public static Disposable SetNormalPriority(this Thread thread)
    {
        return new Disposable(thread, ThreadPriority.Normal);
    }
}
