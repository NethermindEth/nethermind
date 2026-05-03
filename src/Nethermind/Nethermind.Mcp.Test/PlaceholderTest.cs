// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Mcp.Test;

public class PlaceholderTest
{
    [Test]
    public void Project_loads() =>
        Assert.That(typeof(Nethermind.Mcp.PlaceholderType).Namespace, Is.EqualTo("Nethermind.Mcp"));
}
