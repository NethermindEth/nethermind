// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;

namespace Nethermind.State.Flat;

internal static class FlatDbConfigExtensions
{
    /// <summary>
    /// Validates that <see cref="IFlatDbConfig.CompactSize"/> fits in <see cref="int"/> with the small
    /// headroom (+8) used for pool sizing, so the later narrowing cannot overflow.
    /// </summary>
    public static void ValidateCompactSize(this IFlatDbConfig config)
    {
        if (config.CompactSize > int.MaxValue - 8)
            throw new ArgumentOutOfRangeException(nameof(config.CompactSize), "Compact size must not exceed int.MaxValue - 8");
    }
}
