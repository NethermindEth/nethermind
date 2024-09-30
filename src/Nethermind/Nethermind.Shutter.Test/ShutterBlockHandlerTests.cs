// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Nethermind.Core.Test.Builders;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using System;
using Nethermind.Merge.Plugin.Test;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterBlockHandlerTests : EngineModuleTests
{
    [Test]
    public void Can_wait_for_valid_block()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 0);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);
        IShutterBlockHandler blockHandler = api.BlockHandler;

        CancellationTokenSource source = new();
        Task<Block?> waitTask = blockHandler.WaitForBlockInSlot(ShutterTestsCommon.InitialSlot, source.Token);
        Block result = Build.A.Block.WithTimestamp(ShutterTestsCommon.InitialSlotTimestamp).TestObject;
        api.TriggerNewHeadBlock(new(result));

        Assert.That(result, Is.EqualTo(waitTask.Result));
    }

    [Test]
    public async Task Wait_times_out_at_cutoff()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 0);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);

        using CancellationTokenSource source = new();
        Task<Block?> waitTask = api.BlockHandler.WaitForBlockInSlot(ShutterTestsCommon.InitialSlot, source.Token);

        await Task.Delay((int)(api.BlockWaitCutoff.TotalMilliseconds / 2));

        Assert.That(waitTask.IsCompleted, Is.False);

        await Task.Delay((int)(api.BlockWaitCutoff.TotalMilliseconds / 2) + 100);

        Assert.That(waitTask.IsCompletedSuccessfully);
    }

    [Test]
    public void Does_not_wait_after_cutoff()
    {
        const ulong blockWaitCutoff = 1333;
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 2 * blockWaitCutoff);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);

        using CancellationTokenSource source = new();
        Task<Block?> waitTask = api.BlockHandler.WaitForBlockInSlot(ShutterTestsCommon.InitialSlot, source.Token);

        Assert.That(waitTask.IsCompletedSuccessfully);
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
