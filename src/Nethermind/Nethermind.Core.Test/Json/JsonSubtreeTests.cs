// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

public class JsonSubtreeTests
{
    [Test]
    public void Matches_object_subtree()
    {
        string actual = """{"a":1,"b":{"c":2,"d":3}}""";

        Assert.That(actual, JsonSubtree.Containing("""{"b":{"c":2}}"""));
    }

    [Test]
    public void Matches_array_items_as_unordered_subtrees()
    {
        string actual = """[{"a":1,"x":1},{"a":2,"x":2}]""";

        Assert.That(actual, JsonSubtree.Containing("""[{"a":2},{"a":1}]"""));
    }

    [Test]
    public void Requires_distinct_array_item_matches_for_duplicates()
    {
        string actual = """[{"a":1}]""";

        Assert.That(actual, Is.Not.Matches(JsonSubtree.Containing("""[{"a":1},{"a":1}]""")));
    }
}
