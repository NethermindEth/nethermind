// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class Timestamper : ITimestamper
    {
        private readonly DateTime? _constantDate;

        public Timestamper(DateTime? constantDate = null)
        {
            _constantDate = constantDate;
        }

        public DateTime UtcNow => _constantDate ?? DateTime.UtcNow;

        public static readonly ITimestamper Default = new Timestamper();
    }
}
