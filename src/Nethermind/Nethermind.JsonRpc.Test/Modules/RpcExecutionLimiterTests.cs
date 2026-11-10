// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class RpcExecutionLimiterTests
{
    [TestCase(3, 0, 0, 3)]
    [TestCase(3, 0, -1, 3)]
    [TestCase(3, 0, 8, 3)]
    [TestCase(8, 6, 8, 3)]
    [TestCase(8, 6, 0, 3)]
    [TestCase(8, 1, 8, 1)]
    [TestCase(8, 0, 2, 2)]
    [TestCase(8, 6, null, null)]
    public void Execution_limit_retains_module_and_shared_headroom(int moduleLimit, int sharedLimit, int? executionLimit, int? expected)
    {
        JsonRpcConfig config = new()
        {
            EthModuleConcurrentInstances = moduleLimit,
            MaxConcurrentSharedRequests = sharedLimit,
            MaxConcurrentExecutionRequests = executionLimit
        };
        Assert.That(RpcExecutionLimiter.GetConcurrency(config), Is.EqualTo(expected));
    }

    [Test]
    public async Task Unset_execution_limit_admits_every_call()
    {
        RpcExecutionLimiter limiter = new(new JsonRpcConfig { EthModuleConcurrentInstances = 1, Timeout = 0 });
        for (int i = 0; i < 3; i++)
        {
            ValueTask entered = limiter.EnterAsync();
            Assert.That(entered.IsCompletedSuccessfully, Is.True);
            await entered;
        }
        for (int i = 0; i < 3; i++)
        {
            limiter.Exit();
        }
    }

    [Test]
    public async Task Full_execution_and_queue_reject_without_losing_the_next_permit()
    {
        RpcExecutionLimiter limiter = new(1, 5_000, new RpcLimits(queuedLimit: 1));
        await limiter.EnterAsync();
        Task queued = limiter.EnterAsync().AsTask();
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(queued.IsCompleted, Is.False);
                await Assert.ThrowsAsync<LimitExceededException>(async () => await limiter.EnterAsync());
            }
        }
        finally
        {
            limiter.Exit();
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            limiter.Exit();
        }

        await limiter.EnterAsync();
        Task next = limiter.EnterAsync().AsTask();
        Assert.That(next.IsCompleted, Is.False, "admission must remain bounded after rejection and handoff");
        limiter.Exit();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        limiter.Exit();
    }

    [TestCase(0, typeof(ModuleRentalTimeoutException))]
    [TestCase(-2, typeof(ArgumentOutOfRangeException))]
    public async Task Failed_wait_releases_its_shared_queue_slot(int timeout, Type expectedException)
    {
        RpcExecutionLimiter limiter = new(1, timeout, new RpcLimits(queuedLimit: 1));
        await limiter.EnterAsync();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                await Assert.ThrowsAsync(expectedException, async () => await limiter.EnterAsync());
            }
        }
        finally
        {
            limiter.Exit();
        }
    }

    [Test]
    public async Task Available_execution_does_not_need_a_slot_in_an_already_full_shared_queue()
    {
        RpcLimits limits = new(queuedLimit: 1);
        RpcExecutionLimiter busy = new(1, 5_000, limits);
        RpcExecutionLimiter available = new(1, 5_000, limits);
        await busy.EnterAsync();
        Task queued = busy.EnterAsync().AsTask();
        try
        {
            await available.EnterAsync();
            Task next = available.EnterAsync().AsTask();
            await Assert.ThrowsAsync<LimitExceededException>(async () => await next);
            available.Exit();
        }
        finally
        {
            busy.Exit();
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            busy.Exit();
        }
    }
}
