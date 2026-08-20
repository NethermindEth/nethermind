// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class AcceptTxResultTests
{
    private static (string Name, AcceptTxResult Value)[] DeclaredResults { get; } = typeof(AcceptTxResult)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static f => f.FieldType == typeof(AcceptTxResult))
        .Select(static f => (f.Name, (AcceptTxResult)f.GetValue(null)!))
        .ToArray();

    /// <remarks>
    /// Ids are now handed out in declaration order, so a freshly declared result cannot collide. What stays
    /// hand-written, and so still worth guarding, is a declaration that aliases another result — most easily
    /// one built with <see cref="AcceptTxResult.WithMessage"/>, which keeps its origin's id by design.
    /// </remarks>
    [Test]
    public void Every_result_is_distinguishable_from_every_other()
    {
        IEnumerable<string> collisions = DeclaredResults
            .GroupBy(static r => r.Value)
            .Where(static g => g.Count() > 1)
            .Select(static g => string.Join(" == ", g.Select(static r => r.Name)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeclaredResults, Is.Not.Empty, "reflection must actually find the results");
            Assert.That(collisions, Is.Empty);
        }
    }

    [Test]
    public void Separately_declared_results_are_distinct_even_when_they_share_a_code()
    {
        AcceptTxResult first = new("same code");
        AcceptTxResult second = new("same code");

        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public void WithMessage_keeps_the_result_equal_to_the_one_it_came_from()
    {
        foreach ((string name, AcceptTxResult result) in DeclaredResults)
        {
            AcceptTxResult detailed = result.WithMessage("some detail");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(detailed, Is.EqualTo(result), name);
                Assert.That(detailed.GetHashCode(), Is.EqualTo(result.GetHashCode()), name);
                Assert.That(detailed.ToString(), Does.EndWith("some detail"), name);
            }
        }
    }

    [Test]
    public void Only_accepted_converts_to_true()
    {
        foreach ((string name, AcceptTxResult result) in DeclaredResults)
        {
            bool isAccepted = result;

            Assert.That(isAccepted, Is.EqualTo(result == AcceptTxResult.Accepted), name);
        }
    }

    /// <remarks>Ids are handed out from zero in declaration order, so this holds only while
    /// <see cref="AcceptTxResult.Accepted"/> is declared first.</remarks>
    [Test]
    public void Default_result_is_accepted()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(default(AcceptTxResult), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That((bool)default(AcceptTxResult), Is.True);
        }
    }

    [Test]
    public void Bool_converts_to_accepted_or_invalid()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That((AcceptTxResult)true, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That((AcceptTxResult)false, Is.EqualTo(AcceptTxResult.Invalid));
        }
    }
}
