// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

public class JsonNUnitConstraintExtensionsTests
{
    [Test]
    public void ContainSubtree_allows_extra_actual_properties()
    {
        string actual = """[{"a":1,"b":2},{"a":3,"b":4}]""";
        string expected = """[{"a":3},{"a":1}]""";

        Assert.That(JToken.Parse(actual), Does.ContainSubtree(expected));
    }

    [Test]
    public void ContainSubtree_requires_distinct_actual_matches()
    {
        string actual = """[{"a":1,"b":2},{"a":2,"b":3}]""";
        string expected = """[{"a":1},{"a":1}]""";

        Assert.That(
            () => Assert.That(JToken.Parse(actual), Does.ContainSubtree(expected)),
            Throws.TypeOf<AssertionException>());
    }
}
