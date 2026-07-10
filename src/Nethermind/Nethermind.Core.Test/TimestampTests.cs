// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class TimestampTests
    {
        private static readonly DateTime Jan1St1970 = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Test]
        public void epoch_timestamp_in_seconds_and_milliseconds_should_be_valid()
        {
            // Use the same integer-precision path as the implementation. TimeSpan.TotalMilliseconds
            // returns a double and loses sub-millisecond precision once ticks exceed 2^53 (mid-2255),
            // so feeding it through (ulong) was flaking off-by-one against DateTimeOffset.ToUnixTimeMilliseconds.

            DateTime utcNow = DateTime.UtcNow;
            ITimestamper timestamper = new Timestamper(utcNow);
            DateTimeOffset utcNowOffset = new(utcNow);
            ulong epochSeconds = timestamper.UnixTime.Seconds;
            ulong epochMilliseconds = timestamper.UnixTime.Milliseconds;
            ulong unixUtcUntilNowSeconds = (ulong)utcNowOffset.ToUnixTimeSeconds();
            ulong unixUtcUntilNowMilliseconds = (ulong)utcNowOffset.ToUnixTimeMilliseconds();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(epochSeconds, Is.EqualTo(unixUtcUntilNowSeconds));
                Assert.That(epochMilliseconds, Is.EqualTo(unixUtcUntilNowMilliseconds));
            }
        }
    }
}
