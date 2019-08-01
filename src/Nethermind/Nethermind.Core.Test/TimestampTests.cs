/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
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
            var utcNow = DateTime.UtcNow;
            var timestamper = new Timestamper(utcNow);
            var epochSeconds = timestamper.EpochSeconds;
            var epochMilliseconds = timestamper.EpochMilliseconds;
            var unixUtcUntilNowSeconds = (ulong) utcNow.Subtract(Jan1St1970).TotalSeconds;
            var unixUtcUntilNowMilliseconds = (ulong) utcNow.Subtract(Jan1St1970).TotalMilliseconds;

            epochSeconds.Should().Be(unixUtcUntilNowSeconds);
            epochMilliseconds.Should().Be(unixUtcUntilNowMilliseconds);
        }
    }
}