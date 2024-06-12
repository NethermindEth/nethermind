// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;

public static class ThreadExtensions
{
    public readonly struct Disposable : IDisposable
    {
        private readonly Thread _thread;
        private readonly ThreadPriority _previousPriority;

        internal Disposable(Thread thread)
        {
            _thread = thread;
            _previousPriority = thread.Priority;
            thread.Priority = ThreadPriority.AboveNormal;
        }

        public void Dispose()
        {
            _thread.Priority = _previousPriority;
        }
    }

    public static Disposable BoostPriority(this Thread thread)
    {
        return new Disposable(thread);
    }
}
