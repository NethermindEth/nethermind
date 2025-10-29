// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Blockchain;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class TestBlockchainTester
{
    [Test]
    public async Task SampleTest1()
    {
        XdcTestBlockchain? testBlockchain = await XdcTestBlockchain.Create();
        Assert.That(testBlockchain, Is.Not.Null);
        // Add your test assertions here
    }

    [Test]
    public async Task SampleTest2()
    {
        TestBlockchain testBlockchain = await BasicTestBlockchain.Create();
        Assert.That(testBlockchain, Is.Not.Null);
        // Add your test assertions here
    }

}
