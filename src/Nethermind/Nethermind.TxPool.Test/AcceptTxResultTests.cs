// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
public class AcceptTxResultTests
{
    [Test]
    public void Every_declared_result_is_distinguishable_from_every_other()
    {
        (string Name, AcceptTxResult Value)[] results = typeof(AcceptTxResult)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(AcceptTxResult))
            .Select(static field => (field.Name, (AcceptTxResult)field.GetValue(null)!))
            .ToArray();

        List<string> collisions = [];
        for (int i = 0; i < results.Length; i++)
        {
            for (int j = i + 1; j < results.Length; j++)
            {
                if (results[i].Value.Equals(results[j].Value))
                {
                    collisions.Add($"{results[i].Name} == {results[j].Name}");
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.Not.Empty, "reflection must find the declared results, or this test pins nothing");
            // Identity is the id alone, so a reused id silently makes two rejections compare and hash equal.
            Assert.That(collisions, Is.Empty);
        }
    }
}
