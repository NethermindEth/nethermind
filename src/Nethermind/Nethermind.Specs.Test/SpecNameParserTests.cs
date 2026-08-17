// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

public class SpecNameParserTests
{
    [Test]
    public void Parse_maps_Bogota_to_the_frame_transactions_fork()
    {
        IReleaseSpec spec = SpecNameParser.Parse("Bogota");

        Assert.That(spec.IsEip8141Enabled, Is.True);
    }

    [Test]
    public void Parse_names_the_offending_fork_when_unmapped()
    {
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => SpecNameParser.Parse("NotAFork"));

        Assert.That(e.Message, Does.Contain("NotAFork"));
    }
}
