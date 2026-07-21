// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Facade.Simulate;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Simulate;

[Parallelizable(ParallelScope.All)]
public class SimulateReadOnlyBlocksProcessingEnvPoolTests
{
    [Test]
    public void Begin_AtActiveCap_ThrowsConcurrencyLimitReached()
    {
        FakeEnvFactory factory = new();
        using SimulateReadOnlyBlocksProcessingEnvPool pool = new(factory.Create, maxConcurrent: 2);

        SimulateReadOnlyBlocksProcessingEnvPool.PooledScope a = pool.Begin(null);
        SimulateReadOnlyBlocksProcessingEnvPool.PooledScope b = pool.Begin(null);
        try
        {
            Assert.That(() => pool.Begin(null).Dispose(), Throws.TypeOf<ConcurrencyLimitReachedException>());
            Assert.That(factory.Created, Is.EqualTo(2), "factory must not run when the cap is already reached");
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    [Test]
    public void ConcurrentRenters_GetIndependentEnvs()
    {
        FakeEnvFactory factory = new();
        using SimulateReadOnlyBlocksProcessingEnvPool pool = new(factory.Create, maxConcurrent: 4);

        SimulateReadOnlyBlocksProcessingEnvPool.PooledScope a = pool.Begin(null);
        SimulateReadOnlyBlocksProcessingEnvPool.PooledScope b = pool.Begin(null);
        try
        {
            Assert.That(factory.Created, Is.EqualTo(2));
            Assert.That(ReferenceEquals(a.Scope, b.Scope), Is.False);
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Rent_AfterRelease_RestoresSlot(bool poison)
    {
        FakeEnvFactory factory = new(throwOnBegin: poison);
        using SimulateReadOnlyBlocksProcessingEnvPool pool = new(factory.Create, maxConcurrent: 1);

        if (poison)
        {
            Assert.That(() => pool.Begin(null).Dispose(), Throws.TypeOf<InvalidOperationException>());
            Assert.That(factory.DisposedCount, Is.EqualTo(1), "a poisoned env is dropped, not returned to the pool");
            factory.SetThrowOnBegin(false);
        }
        else
        {
            pool.Begin(null).Dispose();
        }

        Assert.That(() => pool.Begin(null).Dispose(), Throws.Nothing);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Dispose_DisposesTrackedEnvs(bool stillRented)
    {
        FakeEnvFactory factory = new();
        SimulateReadOnlyBlocksProcessingEnvPool pool = new(factory.Create, maxConcurrent: 2);

        SimulateReadOnlyBlocksProcessingEnvPool.PooledScope scope = pool.Begin(null);
        if (!stillRented) scope.Dispose();

        pool.Dispose();
        Assert.That(factory.DisposedCount, Is.EqualTo(1));

        if (stillRented)
        {
            scope.Dispose();
            Assert.That(factory.DisposedCount, Is.EqualTo(1), "returning a rented env after shutdown is a no-op");
        }
    }

    [Test]
    public void SequentialRenters_ReuseEnvAndRebegin()
    {
        FakeEnvFactory factory = new();
        using SimulateReadOnlyBlocksProcessingEnvPool pool = new(factory.Create, maxConcurrent: 2);

        pool.Begin(null).Dispose();
        pool.Begin(null).Dispose();

        Assert.That(factory.Created, Is.EqualTo(1), "sequential rents reuse the idle env instead of growing");
        Assert.That(factory.Envs[0].BeginCount, Is.EqualTo(2), "each rent re-begins the env, resetting its temp state");
    }

    private sealed class FakeEnvFactory(bool throwOnBegin = false)
    {
        private bool _throwOnBegin = throwOnBegin;

        public List<FakeEnv> Envs { get; } = [];

        public int Created => Envs.Count;

        public int DisposedCount
        {
            get
            {
                int n = 0;
                foreach (FakeEnv env in Envs) if (env.IsDisposed) n++;
                return n;
            }
        }

        public ISimulateReadOnlyBlocksProcessingEnv Create()
        {
            FakeEnv env = new(_throwOnBegin);
            Envs.Add(env);
            return env;
        }

        public void SetThrowOnBegin(bool value) => _throwOnBegin = value;
    }

    private sealed class FakeEnv(bool throwOnBegin) : ISimulateReadOnlyBlocksProcessingEnv, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public int BeginCount { get; private set; }

        public SimulateReadOnlyBlocksProcessingScope Begin(BlockHeader? baseBlock)
        {
            BeginCount++;
            if (throwOnBegin) throw new InvalidOperationException("simulated begin failure");
            return new SimulateReadOnlyBlocksProcessingScope(
                null!, null!, null!, null!, null!, null!,
                Substitute.For<IReadOnlyDbProvider>(),
                new NoopDisposable());
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
