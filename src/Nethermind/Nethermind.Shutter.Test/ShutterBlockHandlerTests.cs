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
    // slot 10 timestamp
    private readonly ulong _slotTimestamp = ShutterTestsCommon.GenesisTimestamp + 10 * (ulong)ShutterApi.SlotLength.TotalSeconds;
    [Test]
    public async Task Can_wait_for_valid_block()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(_slotTimestamp, 0);

        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);
        IShutterBlockHandler blockHandler = api.BlockHandler;

        bool waitReturned = false;
        CancellationTokenSource source = new();
        _ = blockHandler.WaitForBlockInSlot(10, ShutterApi.SlotLength, ShutterApi.BlockWaitCutoff, source.Token)
            .ContinueWith((_) => waitReturned = true)
            .WaitAsync(source.Token);

        await Task.Delay((int)(ShutterApi.BlockWaitCutoff.TotalMilliseconds / 2));

        Assert.That(waitReturned, Is.False);

        api.TriggerNewHeadBlock(new(Build.A.Block.WithTimestamp(_slotTimestamp).TestObject));

        await Task.Delay(100);
        Assert.That(waitReturned);
    }

    [Test]
    public async Task Wait_times_out_at_cutoff()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(_slotTimestamp, 0);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);

        bool waitReturned = false;
        CancellationTokenSource source = new();
        _ = api.BlockHandler.WaitForBlockInSlot(10, ShutterApi.SlotLength, ShutterApi.BlockWaitCutoff, source.Token)
            .ContinueWith((_) => waitReturned = true)
            .WaitAsync(source.Token);

        await Task.Delay((int)(ShutterApi.BlockWaitCutoff.TotalMilliseconds / 2));

        Assert.That(waitReturned, Is.False);

        await Task.Delay((int)(ShutterApi.BlockWaitCutoff.TotalMilliseconds / 2) + 100);

        Assert.That(waitReturned);
    }

    [Test]
    public async Task Does_not_wait_after_cutoff()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(_slotTimestamp, 2 * (ulong)ShutterApi.BlockWaitCutoff.TotalMilliseconds);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);

        bool waitReturned = false;
        CancellationTokenSource source = new();
        _ = api.BlockHandler.WaitForBlockInSlot(10, ShutterApi.SlotLength, ShutterApi.BlockWaitCutoff, source.Token)
            .ContinueWith((_) => waitReturned = true)
            .WaitAsync(source.Token);

        await Task.Delay(100);
        Assert.That(waitReturned);
    }

    [Test]
    public void Ignores_outdated_block()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(_slotTimestamp, 2 * (ulong)ShutterApi.BlockUpToDateCutoff.TotalMilliseconds);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);

        bool eonUpdateCalled = false;
        CancellationTokenSource source = new();
        api.EonUpdate += (object? sender, EventArgs e) =>
        {
            eonUpdateCalled = true;
        };

        api.TriggerNewHeadBlock(new(Build.A.Block.WithTimestamp(_slotTimestamp).TestObject));
        Assert.That(eonUpdateCalled, Is.False);

        // control: called on up to date block
        api.TriggerNewHeadBlock(new(Build.A.Block.WithTimestamp(_slotTimestamp + 2 * (ulong)ShutterApi.BlockUpToDateCutoff.TotalSeconds).TestObject));
        Assert.That(eonUpdateCalled);
    }

}
