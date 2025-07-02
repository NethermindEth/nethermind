// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class AccountTests
{
    [Test]
    public void Test_totally_empty()
    {
        Account account = Account.TotallyEmpty;
        Assert.That(account.IsTotallyEmpty, Is.True, "totally empty");
        Assert.That(account.IsEmpty, Is.True, "empty");
    }

    [Test]
    public void Test_just_empty()
    {
        Account account = Account.TotallyEmpty;
        account = account.WithChangedStorageRoot(TestItem.KeccakA);
        Assert.That(account.IsTotallyEmpty, Is.False, "totally empty");
        Assert.That(account.IsEmpty, Is.True, "empty");
    }

    [Test]
    public void Test_has_code()
    {
        Account account = Account.TotallyEmpty;
        Assert.That(account.HasCode, Is.False);
        account = account.WithChangedCodeHash(TestItem.KeccakA);
        Assert.That(account.HasCode, Is.True);
    }

    [Test]
    public void Test_has_storage()
    {
        Account account = Account.TotallyEmpty;
        Assert.That(account.HasStorage, Is.False);
        account = account.WithChangedStorageRoot(TestItem.KeccakA);
        Assert.That(account.HasStorage, Is.True);
    }
}
