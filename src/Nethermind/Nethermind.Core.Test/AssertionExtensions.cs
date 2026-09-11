// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public static class AssertionExtensions
{
    public static T AssertSingle<T>(this IReadOnlyList<T> collection, string? message = null,
        [CallerArgumentExpression(nameof(collection))] string? expression = null)
    {
        Assert.That(collection, Has.Count.EqualTo(1), message ?? $"Expected a single element in '{expression}'");
        return collection[0];
    }
}
