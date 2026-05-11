// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public static class JsonTestAssertions
{
    public static void AssertEquivalent(string actualJson, string expectedJson) =>
        AssertEquivalent(JToken.Parse(actualJson), JToken.Parse(expectedJson));

    public static void AssertEquivalent(JToken actual, JToken expected)
    {
        if (AreEquivalent(actual, expected))
        {
            return;
        }

        Assert.Fail($"Expected JSON:{System.Environment.NewLine}{expected}{System.Environment.NewLine}Actual JSON:{System.Environment.NewLine}{actual}");
    }

    public static void AssertNotEquivalent(string actualJson, string expectedJson) =>
        AssertNotEquivalent(JToken.Parse(actualJson), JToken.Parse(expectedJson));

    public static void AssertNotEquivalent(JToken actual, JToken expected)
    {
        if (!AreEquivalent(actual, expected))
        {
            return;
        }

        Assert.Fail($"Expected JSON values to differ:{System.Environment.NewLine}{actual}");
    }

    private static bool AreEquivalent(JToken? actual, JToken? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (actual.Type != expected.Type)
        {
            return false;
        }

        if (actual is JObject actualObject && expected is JObject expectedObject)
        {
            return ObjectsAreEquivalent(actualObject, expectedObject);
        }

        if (actual is JArray actualArray && expected is JArray expectedArray)
        {
            return ArraysAreEquivalent(actualArray, expectedArray);
        }

        return JToken.DeepEquals(actual, expected);
    }

    private static bool ObjectsAreEquivalent(JObject actual, JObject expected)
    {
        int expectedCount = 0;
        foreach (JProperty expectedProperty in expected.Properties())
        {
            expectedCount++;
            if (!actual.TryGetValue(expectedProperty.Name, out JToken? actualValue) ||
                !AreEquivalent(actualValue, expectedProperty.Value))
            {
                return false;
            }
        }

        int actualCount = 0;
        foreach (JProperty _ in actual.Properties())
        {
            actualCount++;
        }

        return actualCount == expectedCount;
    }

    private static bool ArraysAreEquivalent(JArray actual, JArray expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (!AreEquivalent(actual[i], expected[i]))
            {
                return false;
            }
        }

        return true;
    }
}
