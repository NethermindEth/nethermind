// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Tools.Kute;

public sealed class Timer
{
    public TimeSpan Elapsed { get; private set; } = TimeSpan.Zero;

    public IDisposable Time()
    {
        return new Context(this);
    }

    private readonly struct Context : IDisposable
    {
        private readonly Stopwatch _stopwatch;
        private readonly Timer _timer;

        public Context(Timer timer)
        {
            _stopwatch = Stopwatch.StartNew();
            _timer = timer;
        }

        public void Dispose()
        {
            _timer.Elapsed += _stopwatch.Elapsed;
        }
    }
}
