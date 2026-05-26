// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class ReceiptAssertionExtensionsTests
{
    [Test]
    public void UsingReceiptComparer_throws_for_unknown_excluded_property()
    {
        TxReceipt receipt = Build.A.Receipt.TestObject;

        Assert.That(() => Assert.That(receipt, Is.EqualTo(receipt).UsingReceiptComparer("Bogus")),
            Throws.ArgumentException.With.Message.Contains("Unknown TxReceipt property: Bogus"));
    }
}
