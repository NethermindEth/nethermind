// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    /// <summary>
    /// We have used before a construction that was ensuring that the exactly same
    /// timestamp is used when calculating both seconds and milliseconds.
    ///
    /// This struct achieves the same with a slightly neater code.
    /// </summary>
    public readonly struct UnixTime
    {
        private readonly DateTimeOffset _offset;

        public static UnixTime FromSeconds(double seconds)
        {
            return new(DateTime.UnixEpoch.Add(TimeSpan.FromSeconds(seconds)));
        }

        public UnixTime(DateTime dateTime) : this(new DateTimeOffset(dateTime))
        {
        }

        public UnixTime(DateTimeOffset dateTime)
        {
            _offset = dateTime;
        }

        public ulong Seconds => (ulong)SecondsLong;

        public ulong Milliseconds => (ulong)MillisecondsLong;

        public long SecondsLong => _offset.ToUnixTimeSeconds();

        public long MillisecondsLong => _offset.ToUnixTimeMilliseconds();
    }
}
