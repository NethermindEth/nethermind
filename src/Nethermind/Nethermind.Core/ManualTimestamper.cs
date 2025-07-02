// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class ManualTimestamper : ITimestamper
    {
        public ManualTimestamper() : this(DateTime.UtcNow) { }

        public static ManualTimestamper PreMerge
        {
            get
            {
                // Note: Should be new instance as multiple tests tend to mutate it.
                DateTime mergeTime = new DateTime(2022, 9, 15, 13, 45, 0, DateTimeKind.Utc);
                return new ManualTimestamper(mergeTime.AddDays(-1));
            }
        }

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
