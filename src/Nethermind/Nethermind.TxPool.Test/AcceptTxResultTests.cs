// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class AcceptTxResultTests
{
    // Equality is by id alone, so two verdicts sharing one are indistinguishable to callers, and any test
    // asserting on either passes for both.
    [Test]
    public void Every_result_has_a_unique_id()
    {
        Dictionary<AcceptTxResult, string> byId = [];
        List<string> collisions = [];

        foreach (FieldInfo field in typeof(AcceptTxResult).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not AcceptTxResult result) continue;

            if (byId.TryGetValue(result, out string? owner))
            {
                collisions.Add($"{owner} and {field.Name}");
            }
            else
            {
                byId[result] = field.Name;
            }
        }

        Assert.That(collisions, Is.Empty, "these results share an id");
    }
}
