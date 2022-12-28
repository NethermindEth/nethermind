// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core2.Configuration.MockedStart
{
    /// <summary>
    /// Clock that runs at normal pace, but starting at the specified unix time.
    /// </summary>
    public class QuickStartClock : IClock
    {
        private readonly TimeSpan _adjustment;

        public QuickStartClock(long clockOffset)
        {
            _adjustment = TimeSpan.FromSeconds(clockOffset);
        }

        public DateTimeOffset Now() => DateTimeOffset.Now + _adjustment;

        public DateTimeOffset UtcNow() => DateTimeOffset.UtcNow + _adjustment;
    }
}
