// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Timers;

namespace Nethermind.Core.Test;

public class FunctionalTimerFactory(Func<TimeSpan, ITimer> timerFactory): ITimerFactory
{
    public ITimer CreateTimer(TimeSpan interval)
    {
        return timerFactory(interval);
    }
}
