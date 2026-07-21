// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Tools.Kute;

public sealed class Timer
{
    public TimeSpan Elapsed { get; private set; } = TimeSpan.Zero;

    public IDisposable Time() => new Context(this);

    private readonly struct Context(Timer timer) : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly Timer _timer = timer;

        public void Dispose() => _timer.Elapsed += _stopwatch.Elapsed;
    }
}
