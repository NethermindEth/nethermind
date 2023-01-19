// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
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
            // very strange fail once:
            // Failed epoch_timestamp_in_seconds_and_milliseconds_should_be_valid [317 ms]
            // Error Message:
            // Expected value to be 1613321574133UL, but found 1613321574132UL.

            DateTime utcNow = DateTime.UtcNow;
            ITimestamper timestamper = new Timestamper(utcNow);
            ulong epochSeconds = timestamper.UnixTime.Seconds;
            ulong epochMilliseconds = timestamper.UnixTime.Milliseconds;
            ulong unixUtcUntilNowSeconds = (ulong)utcNow.Subtract(Jan1St1970).TotalSeconds;
            ulong unixUtcUntilNowMilliseconds = (ulong)utcNow.Subtract(Jan1St1970).TotalMilliseconds;

            epochSeconds.Should().Be(unixUtcUntilNowSeconds);
            epochMilliseconds.Should().Be(unixUtcUntilNowMilliseconds);
        }
    }
}
