// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class MethodConcurrencyLimitTests
{
    [Test]
    public async Task Waiters_beyond_the_queue_limit_fail_at_once()
    {
        MethodConcurrencyLimit limit = new(1, Timeout.Infinite, new RpcLimits(queuedLimit: 2));
        IRpcModule module = Substitute.For<IRpcModule>();
        Func<bool, CancellationToken, ValueTask<IRpcModule>> rent = limit.WrapRent((_, _) => ValueTask.FromResult(module));
        Action<IRpcModule> returnModule = limit.WrapReturn(static _ => { });
        IRpcModule held = await rent(true, CancellationToken.None);

        ValueTask<IRpcModule> first = rent(true, CancellationToken.None);
        ValueTask<IRpcModule> second = rent(true, CancellationToken.None);
        ValueTask<IRpcModule> refused = rent(true, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(second.IsCompleted, Is.False);
            Assert.That(refused.IsFaulted, Is.True);
        }

        Assert.That(async () => await refused, Throws.TypeOf<LimitExceededException>());

        returnModule(held);
        returnModule(await first.AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.That(await second.AsTask().WaitAsync(TimeSpan.FromSeconds(10)), Is.SameAs(module));
    }

    [Test]
    public async Task Concurrent_callers_never_exceed_the_limit()
    {
        const int slots = 3;
        const int callers = 64;
        const int rounds = 200;
        MethodConcurrencyLimit limit = new(slots, Timeout.Infinite, new RpcLimits());
        IRpcModule module = Substitute.For<IRpcModule>();
        Func<bool, CancellationToken, ValueTask<IRpcModule>> rent = limit.WrapRent((_, _) => ValueTask.FromResult(module));
        Action<IRpcModule> returnModule = limit.WrapReturn(static _ => { });
        int running = 0;
        int maxRunning = 0;
        int completed = 0;

        Task[] workers = new Task[callers];
        for (int c = 0; c < callers; c++)
        {
            workers[c] = Task.Run(async () =>
            {
                for (int i = 0; i < rounds; i++)
                {
                    IRpcModule rented = await rent(true, CancellationToken.None);
                    int now = Interlocked.Increment(ref running);
                    int seen = Volatile.Read(ref maxRunning);
                    while (now > seen && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen)
                    {
                        seen = Volatile.Read(ref maxRunning);
                    }

                    await Task.Yield();
                    Interlocked.Decrement(ref running);
                    Interlocked.Increment(ref completed);
                    returnModule(rented);
                }
            });
        }

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.EqualTo(callers * rounds));
            Assert.That(maxRunning, Is.InRange(1, slots));
        }
    }
}
