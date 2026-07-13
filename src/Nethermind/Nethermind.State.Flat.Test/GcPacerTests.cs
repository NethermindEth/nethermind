// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class GcPacerTests
{
    [Test]
    public void Start_SecondCall_IsNoOp()
    {
        GcPacer.Start(600_000, 0, 0, 0);
        Assert.That(GcPacer.Start(600_000, 0, 0, 0), Is.False);
    }
}
