// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class DbAddressTests
{
    [Test]
    public void Equality()
    {
        DbAddress a1 = DbAddress.Page(1);
        DbAddress a2 = DbAddress.Page(2);

        using (Assert.EnterMultipleScope())
        {
            // ReSharper disable once EqualExpressionComparison
            Assert.That(Equals(a1, a1), Is.True);
            Assert.That(Equals(a1, a2), Is.False);
        }
    }
}
