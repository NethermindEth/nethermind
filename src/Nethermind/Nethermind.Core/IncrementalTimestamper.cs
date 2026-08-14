// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    /// <summary>
    /// Each time this timestamper is asked about the time it move the time forward by some constant
    /// </summary>
    public class IncrementalTimestamper(DateTime initialValue, TimeSpan increment) : ITimestamper
    {
        private readonly TimeSpan _increment = increment;
        private DateTime _utcNow = initialValue;

        public IncrementalTimestamper()
            : this(DateTime.UtcNow, TimeSpan.FromSeconds(1)) { }

        public DateTime UtcNow
        {
            get
            {
                DateTime result = _utcNow;
                _utcNow = _utcNow.Add(_increment);
                return result;
            }
        }
    }
}
