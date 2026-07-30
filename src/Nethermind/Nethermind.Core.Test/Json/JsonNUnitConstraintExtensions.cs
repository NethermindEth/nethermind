// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Core.Test.Json;

public static class JsonNUnitConstraintExtensions
{
    extension(Does)
    {
        public static Constraint ContainSubtree(string expectedJson)
        {
            ArgumentNullException.ThrowIfNull(expectedJson);

            return new JsonSubtreeConstraint(JToken.Parse(expectedJson));
        }

        public static Constraint ContainSubtree(JToken expected)
        {
            ArgumentNullException.ThrowIfNull(expected);

            return new JsonSubtreeConstraint(expected);
        }
    }

    private static bool ContainsSubtree(JToken actual, JToken expected)
    {
        if (JToken.DeepEquals(actual, expected))
        {
            return true;
        }

        if (actual is JObject actualObject && expected is JObject expectedObject)
        {
            foreach (JProperty expectedProperty in expectedObject.Properties())
            {
                if (!actualObject.TryGetValue(expectedProperty.Name, out JToken? actualValue) ||
                    !ContainsSubtree(actualValue, expectedProperty.Value))
                {
                    return false;
                }
            }

            return true;
        }

        if (actual is JArray actualArray && expected is JArray expectedArray)
        {
            bool[] matchedActualItems = new bool[actualArray.Count];
            foreach (JToken expectedItem in expectedArray)
            {
                int matchIndex = FindMatchingSubtree(actualArray, matchedActualItems, expectedItem);
                if (matchIndex == -1)
                {
                    return false;
                }

                matchedActualItems[matchIndex] = true;
            }

            return true;
        }

        return false;
    }

    private static int FindMatchingSubtree(JArray actualArray, bool[] matchedActualItems, JToken expected)
    {
        for (int i = 0; i < actualArray.Count; i++)
        {
            if (matchedActualItems[i])
            {
                continue;
            }

            if (ContainsSubtree(actualArray[i], expected))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class JsonSubtreeConstraint(JToken expected) : Constraint
    {
        public override string Description => $"JSON containing subtree {expected.ToString(Formatting.None)}";

        public override ConstraintResult ApplyTo<TActual>(TActual actual)
        {
            if (!TryGetToken(actual, out JToken? actualToken))
            {
                return new ConstraintResult(this, actual, false);
            }

            return new ConstraintResult(this, actualToken, ContainsSubtree(actualToken, expected));
        }

        private static bool TryGetToken<TActual>(TActual actual, [NotNullWhen(true)] out JToken? token)
        {
            switch (actual)
            {
                case JToken actualToken:
                    token = actualToken;
                    return true;
                case string actualJson:
                    try
                    {
                        token = JToken.Parse(actualJson);
                        return true;
                    }
                    catch (JsonReaderException)
                    {
                        token = null;
                        return false;
                    }
                default:
                    token = null;
                    return false;
            }
        }
    }
}
