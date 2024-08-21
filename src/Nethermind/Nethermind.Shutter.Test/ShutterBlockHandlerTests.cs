// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using System;
using Nethermind.Core.Test.Builders;
using System.Threading;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterBlockHandlerTests
{
    [Test]
    public void Can_wait_for_valid_block()
    {
        IShutterBlockHandler blockHandler = ShutterTestsCommon.InitApi().BlockHandler;

        bool waitReturned = false;
        CancellationTokenSource source = new();
        blockHandler.WaitForBlockInSlot(10, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), source.Token)
            .ContinueWith((_) => waitReturned = true)
            .WaitAsync(source.Token);
        
        blockHandler.OnNewHeadBlock(Build.A.Block.TestObject);

        Assert.That(waitReturned);
    }

    [Test]
    public void Ignores_outdated_block()
    {

    }

}
