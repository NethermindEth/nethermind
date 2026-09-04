// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

/// <summary>TEMPORARY — hangs on purpose to exercise the CI hang dump. Reverted before review.</summary>
public class HangDumpProbe
{
    [Test]
    public void Hangs_forever() => Thread.Sleep(Timeout.Infinite);
}
