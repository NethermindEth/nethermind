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
[Parallelizable(ParallelScope.All)]
class ShutterBlockHandlerTests : BaseEngineModuleTests
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
        using CancellationTokenSource timeoutSource = new();
        Task<Block?> waitTask = api.BlockHandler.WaitForBlockInSlot(ShutterTestsCommon.InitialSlot, source.Token, (int waitTime) =>
        {
            Assert.That(waitTime, Is.EqualTo((int)api.BlockWaitCutoff.TotalMilliseconds));
            return timeoutSource;
        });

        Assert.That(waitTask.IsCompleted, Is.False);
        timeoutSource.Cancel();
        Block? result = await waitTask;
        Assert.That(result, Is.Null);
    }

    [Test]
    public void Does_not_wait_after_cutoff()
    {
        ulong blockWaitCutoff = ShutterTestsCommon.Cfg.BlockUpToDateCutoff;
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

    [Test]
    public void New_head_does_not_deadlock_with_concurrent_timeout()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 0);
        ShutterApiSimulator api = ShutterTestsCommon.InitApi(rnd, timestamper);

        using CancellationTokenSource source = new();
        using CancelOnDisposeCts timeoutSource = new();
        Task<Block?> waitTask = api.BlockHandler.WaitForBlockInSlot(ShutterTestsCommon.InitialSlot, source.Token, _ => timeoutSource);
        timeoutSource.ArmAfterWaitRegistered();

        Block head = Build.A.Block.WithTimestamp(ShutterTestsCommon.InitialSlotTimestamp).TestObject;
        Task newHead = Task.Run(() => api.TriggerNewHeadBlock(new(head)));

#pragma warning disable NUnit2045 // Completion must be confirmed before reading the result; .Result has no timeout.
        Assert.That(newHead.Wait(TimeSpan.FromSeconds(10)), Is.True, "NewHeadBlock handler deadlocked against the timeout callback");
#pragma warning restore NUnit2045
        Assert.That(waitTask.Result, Is.EqualTo(head));
    }

    /// <summary>
    /// Timeout source whose Dispose (called by the handler under its lock without the fix) fires the timeout on
    /// another thread and returns only once the handler's timeout callback is blocked on that lock, or has finished.
    /// </summary>
    private sealed class CancelOnDisposeCts : CancellationTokenSource
    {
        private readonly ManualResetEventSlim _callbackStarted = new();
        private Thread? _cancelThread;
        private bool _armed;

        public void ArmAfterWaitRegistered()
        {
            // Registered after the handler's callback, so it runs first (LIFO) on the cancel thread and the
            // handler's callback runs next on that same thread.
            Token.Register(() =>
            {
                _cancelThread = Thread.CurrentThread;
                _callbackStarted.Set();
            });
            _armed = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (_armed)
            {
                _armed = false;
                Task cancel = Task.Run(Cancel);
                _callbackStarted.Wait();
                // Without the fix the handler's callback blocks on the lock held by this thread; with the fix it
                // finds its entry already removed and the cancel completes.
                SpinWait.SpinUntil(
                    () => cancel.IsCompleted || (_cancelThread!.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(10));
            }

            if (disposing)
            {
                _callbackStarted.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
