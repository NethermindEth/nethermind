//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class TimestampTests
    {
        private static readonly DateTime Jan1St1970 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
            ulong unixUtcUntilNowSeconds = (ulong) utcNow.Subtract(Jan1St1970).TotalSeconds;
            ulong unixUtcUntilNowMilliseconds = (ulong) utcNow.Subtract(Jan1St1970).TotalMilliseconds;

            epochSeconds.Should().Be(unixUtcUntilNowSeconds);
            epochMilliseconds.Should().Be(unixUtcUntilNowMilliseconds);
        }
    }
}
