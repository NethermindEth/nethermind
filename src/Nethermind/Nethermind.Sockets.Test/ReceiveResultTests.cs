// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Nethermind.Sockets.Test;

public class ReceiveResultTests
{
    [Test]
    public void Packs_into_eight_bytes() => Assert.That(Unsafe.SizeOf<ReceiveResult>(), Is.EqualTo(8));

    [Test]
    public void Default_is_null_and_constructed_is_not()
    {
        ReceiveResult result = new() { Read = 5, EndOfMessage = true, Closed = true };

        Assert.That(default(ReceiveResult).IsNull, Is.True);
        Assert.That(result.IsNull, Is.False);
        Assert.That(result.Read, Is.EqualTo(5));
        Assert.That(result.EndOfMessage, Is.True);
        Assert.That(result.Closed, Is.True);
    }
}
