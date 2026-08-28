// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class AcceptTxResultTests
{
    /// <remarks>Equality is by id alone, so two results sharing an id are indistinguishable and a filter test
    /// can pass while comparing against the wrong one.</remarks>
    [Test]
    public void Every_result_is_distinguishable_from_every_other()
    {
        (string Name, AcceptTxResult Value)[] results = typeof(AcceptTxResult)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static f => f.FieldType == typeof(AcceptTxResult))
            .Select(static f => (f.Name, (AcceptTxResult)f.GetValue(null)!))
            .ToArray();

        IEnumerable<string> collisions = results
            .GroupBy(static r => r.Value)
            .Where(static g => g.Count() > 1)
            .Select(static g => string.Join(" == ", g.Select(static r => r.Name)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.Not.Empty, "reflection must actually find the results");
            Assert.That(collisions, Is.Empty);
        }
    }
}
