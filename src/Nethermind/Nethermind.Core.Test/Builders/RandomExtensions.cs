// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Crypto;

namespace Nethermind.Core.Test.Builders;

public static class RandomExtensions
{
    public static T NextFrom<T>(this Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];

    public static T NextFrom<T>(this ICryptoRandom random, IReadOnlyList<T> values) => values[random.NextInt(values.Count)];

    /// <summary>Returns a non-negative random <see cref="ulong"/>, mirroring <see cref="Random.NextInt64()"/>.</summary>
    public static ulong NextUInt64(this Random random) => (ulong)random.NextInt64();

    /// <summary>Returns a random <see cref="ulong"/> in <c>[0, maxValue)</c>, mirroring <see cref="Random.NextInt64(long)"/>.</summary>
    public static ulong NextUInt64(this Random random, ulong maxValue) => (ulong)random.NextInt64((long)maxValue);

    /// <summary>Returns a random <see cref="ulong"/> in <c>[minValue, maxValue)</c>, mirroring <see cref="Random.NextInt64(long, long)"/>.</summary>
    public static ulong NextUInt64(this Random random, ulong minValue, ulong maxValue) => (ulong)random.NextInt64((long)minValue, (long)maxValue);
}
