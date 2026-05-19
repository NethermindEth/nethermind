// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework.Constraints;

namespace Nethermind.Core.Test.Json;

/// <summary>
/// NUnit constraints and helpers for asserting that JSON contains a structural subtree.
/// </summary>
public static class JsonSubtree
{
    /// <summary>
    /// Builds a constraint that succeeds when the actual JSON contains <paramref name="expectedJson"/>.
    /// </summary>
    public static Constraint Containing(string expectedJson)
    {
        ArgumentNullException.ThrowIfNull(expectedJson);

        return new JsonSubtreeConstraint(JToken.Parse(expectedJson));
    }

    /// <summary>
    /// Builds a constraint that succeeds when the actual JSON contains <paramref name="expected"/>.
    /// </summary>
    public static Constraint Containing(JToken expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        return new JsonSubtreeConstraint(expected);
    }

    /// <summary>
    /// Extends NUnit constraint expressions with a JSON subtree constraint.
    /// </summary>
    public static Constraint ContainSubtree(this ConstraintExpression expression, string expectedJson)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return expression.Append(Containing(expectedJson));
    }

    /// <summary>
    /// Extends NUnit constraint expressions with a JSON subtree constraint.
    /// </summary>
    public static Constraint ContainSubtree(this ConstraintExpression expression, JToken expected)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return expression.Append(Containing(expected));
    }

    /// <summary>
    /// Returns whether <paramref name="actual"/> contains <paramref name="expected"/> as a structural JSON subtree.
    /// </summary>
    public static bool Contains(JToken actual, JToken expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);

        return ContainsSubtree(actual, expected);
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
            bool[] matched = new bool[actualArray.Count];
            foreach (JToken expectedItem in expectedArray)
            {
                bool found = false;
                for (int i = 0; i < actualArray.Count; i++)
                {
                    if (matched[i])
                    {
                        continue;
                    }

                    if (ContainsSubtree(actualArray[i], expectedItem))
                    {
                        matched[i] = true;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        return false;
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
