// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class ValidTests
{
    private static IEnumerable<TestCaseData> TestCases()
    {
        yield return new(new[] { Valid.Never, Valid.Never }, "Valid: never");
        yield return new(new[] { Valid.Since(10), Valid.Never }, "Valid: since 10");
        yield return new(new[] { Valid.Before(10), Valid.Never }, "Valid: before 10");
        yield return new(new[] { Valid.Between(10, 20), Valid.Never }, "Valid: between 10 and 20");

        yield return new(new[] { Valid.Never, Valid.Always }, "Valid: always");
        yield return new(new[] { Valid.Always, Valid.Always }, "Valid: always");
        yield return new(new[] { Valid.Always, Valid.Always }, "Valid: always");
        yield return new(new[] { Valid.Since(10), Valid.Always }, "Valid: always");
        yield return new(new[] { Valid.Before(10), Valid.Always }, "Valid: always");
        yield return new(new[] { Valid.Between(10, 20), Valid.Always }, "Valid: always");

        yield return new(new[] { Valid.Before(10), Valid.Since(20) }, "Valid: before 10; since 20");
        yield return new(new[] { Valid.Before(10), Valid.Since(10) }, "Valid: always");
        yield return new(new[] { Valid.Before(20), Valid.Since(10) }, "Valid: always");

        yield return new(new[] { Valid.Between(10, 20), Valid.Since(30) }, "Valid: between 10 and 20; since 30");
        yield return new(new[] { Valid.Between(10, 20), Valid.Since(20) }, "Valid: since 10");
        yield return new(new[] { Valid.Between(10, 30), Valid.Since(20) }, "Valid: since 10");

        yield return new(new[] { Valid.Between(10, 20), Valid.Between(30, 40) }, "Valid: between 10 and 20; between 30 and 40");
        yield return new(new[] { Valid.Between(10, 20), Valid.Between(20, 40) }, "Valid: between 10 and 40");
        yield return new(new[] { Valid.Between(10, 30), Valid.Between(20, 40) }, "Valid: between 10 and 40");
    }

    [TestCaseSource(nameof(TestCases))]
    public void CombinesCorrectly(Valid[] values, string expected)
    {
        Assert.That($"{values.Aggregate((v1, v2) => v1 | v2)}", Is.EqualTo(expected));
        Assert.That($"{values.Reverse().Aggregate((v1, v2) => v1 | v2)}", Is.EqualTo(expected));
    }
}
