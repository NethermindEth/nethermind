// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Cpu;
using NUnit.Framework;

namespace Nethermind.Core.Test.Cpu;

public class ConsoleExitHandlerTests
{
    [Test]
    public void Killing_a_process_that_was_never_started_does_not_throw()
    {
        using Process process = new();
        using ConsoleExitHandler handler = new(process);

        Assert.That(handler.KillProcessTree, Throws.Nothing);
    }
}
