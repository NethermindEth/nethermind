// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Evm.Test.OverridableEnv;

[Parallelizable(ParallelScope.All)]
public class ShareableOverridableEnvSourceTests
{
    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(8, 8)]
    public void BuildAndOverride_AtActiveCap_ThrowsConcurrencyLimitReached(int cap, int held)
    {
        FakeEnvFactory factory = new();
        using ShareableOverridableEnvSource<Marker> source = new(factory.Create, cap);

        List<Scope<Marker>> rented = RentMany(source, held);

        Action overflow = () => source.BuildAndOverride(null);
        Assert.That(overflow, Throws.TypeOf<ConcurrencyLimitReachedException>());
        Assert.That(factory.Created, Is.EqualTo(held), "factory must not run when the cap is already reached");

        foreach (Scope<Marker> scope in rented) scope.Dispose();
    }

    [TestCase(ReleasePath.Healthy)]
    [TestCase(ReleasePath.Poisoned)]
    public void Rent_AfterRelease_RestoresCapSlot(ReleasePath path)
    {
        FakeEnvFactory factory = new(throwOnBuild: path == ReleasePath.Poisoned);
        using ShareableOverridableEnvSource<Marker> source = new(factory.Create, maxConcurrent: 1);

        if (path == ReleasePath.Healthy)
        {
            source.BuildAndOverride(null).Dispose();
        }
        else
        {
            Action poisoned = () => source.BuildAndOverride(null);
            Assert.That(poisoned, Throws.TypeOf<InvalidOperationException>());
            factory.SetThrowOnBuild(false);
        }

        Action retry = () => source.BuildAndOverride(null).Dispose();
        Assert.That(retry, Throws.Nothing);
    }

    [TestCase(ShutdownState.IdleOnly)]
    [TestCase(ShutdownState.StillRented)]
    public void Dispose_DisposesAllTrackedEnvs(ShutdownState state)
    {
        FakeEnvFactory factory = new();
        ShareableOverridableEnvSource<Marker> source = new(factory.Create, maxConcurrent: 2);

        Scope<Marker> scope = source.BuildAndOverride(null);
        if (state == ShutdownState.IdleOnly) scope.Dispose();

        source.Dispose();

        Assert.That(factory.DisposedCount, Is.EqualTo(1));

        if (state == ShutdownState.StillRented)
        {
            Action returnAfterShutdown = scope.Dispose;
            Assert.That(returnAfterShutdown, Throws.Nothing);
            Assert.That(factory.DisposedCount, Is.EqualTo(1), "double dispose is a no-op via the tracking dictionary");
        }
    }

    private static List<Scope<Marker>> RentMany(ShareableOverridableEnvSource<Marker> source, int count)
    {
        List<Scope<Marker>> rented = new(count);
        for (int i = 0; i < count; i++) rented.Add(source.BuildAndOverride(null));
        return rented;
    }

    public enum ReleasePath { Healthy, Poisoned }
    public enum ShutdownState { IdleOnly, StillRented }

    private sealed record Marker;

    private sealed class FakeEnvFactory(bool throwOnBuild = false)
    {
        private bool _throwOnBuild = throwOnBuild;
        private readonly List<FakeEnv> _envs = [];

        public int Created => _envs.Count;
        public int DisposedCount
        {
            get
            {
                int n = 0;
                foreach (FakeEnv env in _envs) if (env.IsDisposed) n++;
                return n;
            }
        }

        public IOverridableEnv<Marker> Create()
        {
            FakeEnv env = new(_throwOnBuild);
            _envs.Add(env);
            return env;
        }

        public void SetThrowOnBuild(bool value) => _throwOnBuild = value;
    }

    private sealed class FakeEnv(bool throwOnBuild) : IOverridableEnv<Marker>, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public Scope<Marker> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride = null, IReleaseSpec? specOverride = null, BlockOverride? blockOverride = null)
        {
            if (throwOnBuild) throw new InvalidOperationException("simulated build failure");
            return new Scope<Marker>(new Marker(), new NoopDisposable());
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
