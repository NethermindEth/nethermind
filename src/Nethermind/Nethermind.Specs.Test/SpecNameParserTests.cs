// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

public class SpecNameParserTests
{
    [Test]
    public void Parse_maps_Bogota_to_the_frame_transactions_fork()
    {
        IReleaseSpec spec = SpecNameParser.Parse("Bogota");

        Assert.That(spec, Is.SameAs(Bogota.Instance));
    }

    [TestCase("NotAFork", "NotAFork")]
    [TestCase("Merge+9999", "Paris+9999")]
    public void Parse_names_the_offending_fork_when_unmapped(string specName, string resolvedSpecName)
    {
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => SpecNameParser.Parse(specName))!;

        Assert.That(e.Message, Does.Contain(specName));
        Assert.That(e.Message, Does.Contain(resolvedSpecName));
    }
}
