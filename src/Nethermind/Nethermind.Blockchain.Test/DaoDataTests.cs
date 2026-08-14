// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class DaoDataTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Test()
    {
        Assert.That(DaoData.DaoAccounts.Length, Is.EqualTo(116));
        Assert.That(DaoData.DaoWithdrawalAccount, Is.EqualTo(new Address("bf4ed7b27f1d666546e30d74d50d173d20bca754")));
    }
}
