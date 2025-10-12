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
}
