// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Reflection;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions;

public class AcceptTxResultAuRaTests
{
    /// <remarks>
    /// Declared outside <see cref="AcceptTxResult"/>, so its id depends on the runtime having initialised
    /// that type first.
    /// </remarks>
    [Test]
    public void Permission_denied_is_distinguishable_from_every_pool_result()
    {
        (string Name, AcceptTxResult Value)[] poolResults = typeof(AcceptTxResult)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static f => f.FieldType == typeof(AcceptTxResult))
            .Select(static f => (f.Name, (AcceptTxResult)f.GetValue(null)!))
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(poolResults, Is.Not.Empty, "reflection must actually find the results");
            foreach ((string name, AcceptTxResult result) in poolResults)
            {
                Assert.That(AcceptTxResultAuRa.PermissionDenied, Is.Not.EqualTo(result), name);
            }
        }
    }
}
