// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Timers;

namespace Nethermind.Core.Timers
{
    public class TimerFactory : ITimerFactory
    {
        public static readonly ITimerFactory Default = new TimerFactory();

        public ITimer CreateTimer(TimeSpan interval) => new
            TimerWrapper(new Timer())
        { Interval = interval };
    }
}
