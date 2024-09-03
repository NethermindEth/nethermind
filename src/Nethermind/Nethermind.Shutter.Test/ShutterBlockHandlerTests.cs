// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Nethermind.Core.Test.Builders;
using System.Threading;
using Nethermind.Merge.Plugin.Test;
using System.Threading.Tasks;
using Nethermind.Core;
using System;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterBlockHandlerTests : EngineModuleTests
{
    [Test]
    public async Task Can_wait_for_valid_block()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 0);

        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);
        IShutterBlockHandler blockHandler = api.BlockHandler;

        bool waitReturned = false;
        CancellationTokenSource source = new();
        _ = blockHandler.WaitForBlockInSlot(ShutterTestsCommon.InitialSlot, source.Token)
            .ContinueWith((_) => waitReturned = true)
            .WaitAsync(source.Token);

        await Task.Delay((int)(api.BlockWaitCutoff.TotalMilliseconds / 2));

        Assert.That(waitReturned, Is.False);

        api.TriggerNewHeadBlock(new(Build.A.Block.WithTimestamp(ShutterTestsCommon.InitialSlotTimestamp).TestObject));

        await Task.Delay(100);
        Assert.That(waitReturned);
    }

    [Test]
    public async Task Wait_times_out_at_cutoff()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 0);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);

        bool waitReturned = false;
        CancellationTokenSource source = new();
        _ = api.BlockHandler.WaitForBlockInSlot(ShutterTestsCommon.InitialSlot, source.Token)
            .ContinueWith((_) => waitReturned = true)
            .WaitAsync(source.Token);

        await Task.Delay((int)(api.BlockWaitCutoff.TotalMilliseconds / 2));

        Assert.That(waitReturned, Is.False);

        await Task.Delay((int)(api.BlockWaitCutoff.TotalMilliseconds / 2) + 100);

        Assert.That(waitReturned);
    }

    [Test]
    public async Task Does_not_wait_after_cutoff()
    {
        const ulong blockWaitCutoff = 1333;
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 2 * blockWaitCutoff);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);

        bool waitReturned = false;
        CancellationTokenSource source = new();
        _ = api.BlockHandler.WaitForBlockInSlot(ShutterTestsCommon.InitialSlot, source.Token)
            .ContinueWith((_) => waitReturned = true)
            .WaitAsync(source.Token);

        await Task.Delay(100);
        Assert.That(waitReturned);
    }

    [Test]
    public void Ignores_outdated_block()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 2 * (ulong)ShutterTestsCommon.BlockUpToDateCutoff.TotalMilliseconds);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);

        // not triggered on outdated block
        api.TriggerNewHeadBlock(new(Build.A.Block.WithTimestamp(ShutterTestsCommon.InitialSlotTimestamp).TestObject));
        Assert.That(api.EonUpdateCalled, Is.EqualTo(0));

        // triggered on up to date block
        ulong upToDateTimestamp = ShutterTestsCommon.InitialSlotTimestamp + 2 * (ulong)ShutterTestsCommon.BlockUpToDateCutoff.TotalSeconds;
        api.TriggerNewHeadBlock(new(Build.A.Block.WithTimestamp(upToDateTimestamp).TestObject));
        Assert.That(api.EonUpdateCalled, Is.EqualTo(1));
    }

}
