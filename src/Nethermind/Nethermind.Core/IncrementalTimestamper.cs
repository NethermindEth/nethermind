// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    /// <summary>
    /// Each time this timestamper is asked about the time it move the time forward by some constant
    /// </summary>
    public class IncrementalTimestamper : ITimestamper
    {
        private readonly TimeSpan _increment;
        private DateTime _utcNow;

        public IncrementalTimestamper()
            : this(DateTime.UtcNow, TimeSpan.FromSeconds(1)) { }

        public IncrementalTimestamper(DateTime initialValue, TimeSpan increment)
        {
            _increment = increment;
            _utcNow = initialValue;
        }

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
