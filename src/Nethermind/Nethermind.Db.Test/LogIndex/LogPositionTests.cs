// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Db.LogIndex;
using NUnit.Framework;

namespace Nethermind.Db.Test.LogIndex;

public class LogPositionTests
{
    [Test]
    public void HasCorrectSize()
    {
        Assert.That(Marshal.SizeOf<LogPosition>(), Is.EqualTo(LogPosition.Size));
    }

    [Test]
    public void IsSortedProperly()
    {
        var blocks = Enumerable.Range(0, 20).ToArray();
        var indexes = Enumerable.Range(0, 20).ToArray();

        LogPosition[] positions = blocks.SelectMany(b => indexes.Select(i => new LogPosition(b, i))).ToArray();

        Assert.That(positions, Is.EqualTo(positions.Order()));
    }

    [Test]
    public void IsSortedProperly_AsLongs()
    {
        var blocks = Enumerable.Range(0, 20).ToArray();
        var indexes = Enumerable.Range(0, 20).ToArray();

        long[] positions = MemoryMarshal.Cast<LogPosition, long>(
            blocks.SelectMany(b => indexes.Select(i => new LogPosition(b, i))).ToArray()
        ).ToArray();

        Assert.That(positions, Is.EqualTo(positions.Order()));
    }
}
