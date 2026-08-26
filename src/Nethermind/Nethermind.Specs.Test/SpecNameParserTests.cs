// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

public class SpecNameParserTests
{
    // Consensus fixtures name their fork through this parser, and the Bogota vectors are the inclusion-list
    // ones. Frame transactions are not part of the name: they carry their own transition timestamp.
    [Test]
    public void Parse_maps_Bogota_to_the_inclusion_lists_fork()
    {
        IReleaseSpec spec = SpecNameParser.Parse("Bogota");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec, Is.SameAs(Bogota.Instance));
            Assert.That(spec.IsEip8037Enabled, Is.True);
            Assert.That(spec.IsEip7805Enabled, Is.True);
            Assert.That(spec.IsEip8141Enabled, Is.False);
        }
    }

    // The frame-transaction fixtures label their fork "Bogota" too, meaning Amsterdam plus EIP-8141 rather
    // than plus EIP-7805. Only the archive tells the two apart, so the runner aliases that name onto this
    // one, which therefore has to resolve.
    [Test]
    public void Parse_maps_Eip8141Prototype_to_the_frame_transaction_fork()
    {
        IReleaseSpec spec = SpecNameParser.Parse("Eip8141Prototype");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec, Is.SameAs(Eip8141Prototype.Instance));
            Assert.That(spec.IsEip8141Enabled, Is.True);
            Assert.That(spec.IsEip7805Enabled, Is.False);
        }
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
