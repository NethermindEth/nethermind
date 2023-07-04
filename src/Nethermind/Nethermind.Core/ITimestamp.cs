// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public interface ITimestamper
    {
        DateTime UtcNow { get; }

        public DateTimeOffset UtcNowOffset => new(UtcNow);

        public UnixTime UnixTime => new(UtcNow);
    }
}
