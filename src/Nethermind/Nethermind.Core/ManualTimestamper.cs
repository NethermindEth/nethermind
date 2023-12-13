// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class ManualTimestamper : ITimestamper
    {
        public ManualTimestamper() : this(DateTime.UtcNow) { }

        public ManualTimestamper(DateTime initialValue)
        {
            UtcNow = initialValue;
        }

        public DateTime UtcNow { get; set; }

        public void Add(TimeSpan timeSpan)
        {
            UtcNow += timeSpan;
        }

        public void Set(DateTime utcNow)
        {
            UtcNow = utcNow;
        }
    }
}
