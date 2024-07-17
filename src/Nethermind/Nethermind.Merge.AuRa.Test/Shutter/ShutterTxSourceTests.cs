// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Merge.AuRa.Shutter;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Merge.AuRa.Test.Shutter;
[TestFixture]
public class ShutterTxSourceTests
{
    [Test]
    public void Test()
    {
        IShutterConfig config = new ShutterConfig()
        {
            Enabled = true,
            
        };
        ShutterTxLoader loader = new(Substitute.For<ILogFinder>(),);
        ShutterTxSource sut = new();
    }
}
