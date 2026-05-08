// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Extensions;

public static class DateTimeExtensions
{
    public static long ToUnixTimeSeconds(this DateTime date)
        => (date - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
}
