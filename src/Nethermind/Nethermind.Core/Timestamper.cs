// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class Timestamper : ITimestamper
    {
        private DateTime? _date;

        public Timestamper(DateTime? date = null)
        {
            _date = date;
        }

        public Timestamper(long? timestamp)
        {
            if (timestamp is not null)
            {
                var blockTime = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
                _date = blockTime.UtcDateTime;
            }
        }

        public DateTime UtcNow => _date ?? DateTime.UtcNow;

        public static readonly ITimestamper Default = new Timestamper();

        public void SetTimestamp(long timestamp)
        {
            var blockTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            SetDate(blockTime.UtcDateTime);
        }

        public void SetDate(DateTime date)
            => _date = date;
    }
}
