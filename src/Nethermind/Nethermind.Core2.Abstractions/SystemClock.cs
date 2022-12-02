// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core2
{
    /// <summary>
    /// Default implementation of IClock that just forwards calls to the system clock.
    /// </summary>
    public class SystemClock : IClock
    {
        public DateTimeOffset Now() => DateTimeOffset.Now;

        public DateTimeOffset UtcNow() => DateTimeOffset.UtcNow;
    }
}
