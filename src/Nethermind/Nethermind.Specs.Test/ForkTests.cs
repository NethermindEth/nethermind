// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

public class ForkTests
{
    [Test]
    public void GetLatest_Returns_Prague()
    {
        Assert.That(Fork.GetLatest(), Is.EqualTo(Prague.Instance));
    }
}
