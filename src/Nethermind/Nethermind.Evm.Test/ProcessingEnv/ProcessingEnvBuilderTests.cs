// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Consensus.Processing;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.ProcessingEnv;

// Wrapper interfaces must be public so the emitted implementation (in a dynamic assembly) can see them.
public interface ITestProcessingEnv : IDisposable
{
    IWorldState WorldState { get; }
    ITransactionProcessor TransactionProcessor { get; }
}

public interface ITrackerEnv : IDisposable
{
    ProcessingEnvBuilderTests.TrackingDisposable Tracker { get; }
}

public interface IAsyncTrackerEnv : IAsyncDisposable
{
    ProcessingEnvBuilderTests.TrackingDisposable Tracker { get; }
}

public interface IEmptyEnv : IDisposable
{
}

public interface IOverridableTestEnv : IDisposable
{
    IWorldState WorldState { get; }
    ICodeInfoRepository CodeInfoRepository { get; }
}

public interface IEnvWithMethod : IDisposable
{
    void DoSomething();
}

// Inherits IOverridableEnv<T>.BuildAndOverride, which the wrapper forwards to the resolved env.
public interface IOverridableWorldStateEnv : IOverridableEnv<IWorldState>, IAsyncDisposable
{
}

// Uses the Null placeholder component; the wrapper forwards BuildAndOverride and resolves the getter.
public interface INullComponentEnv : IOverridableEnv<Null>, IDisposable
{
    IWorldState WorldState { get; }
}

// No IDisposable: only valid with OwnedByParentLifetime, which hands scope disposal to the parent.
public interface IOwnedTrackerEnv
{
    ProcessingEnvBuilderTests.TrackingDisposable Tracker { get; }
}

[Parallelizable(ParallelScope.All)]
public class ProcessingEnvBuilderTests
{
    private static IContainer BuildContainer() =>
        new ContainerBuilder().AddModule(new TestNethermindModule()).Build();

    [Test]
    public void BuildAs_binds_world_state_and_replaced_component()
    {
        using IContainer container = BuildContainer();
        IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().CreateResettableWorldState();
        ITransactionProcessor fakeProcessor = Substitute.For<ITransactionProcessor>();

        using ITestProcessingEnv env = container.Resolve<IProcessingEnvBuilder>()
            .WithWorldState(provider)
            .WithReplacedComponent<ITransactionProcessor>(fakeProcessor)
            .BuildAs<ITestProcessingEnv>();

        Assert.That(env.TransactionProcessor, Is.SameAs(fakeProcessor));
        Assert.That(env.WorldState.ScopeProvider, Is.SameAs(provider));
    }

    [Test]
    public void Dispose_disposes_the_child_scope()
    {
        using IContainer container = BuildContainer();

        TrackingDisposable tracker;
        using (ITrackerEnv env = container.Resolve<IProcessingEnvBuilder>()
                   .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
                   .Configure(builder => builder.AddScoped<TrackingDisposable>())
                   .BuildAs<ITrackerEnv>())
        {
            tracker = env.Tracker; // force construction inside the scope
            Assert.That(tracker.Disposed, Is.False);
        }

        Assert.That(tracker.Disposed, Is.True);
    }

    [Test]
    public async Task DisposeAsync_disposes_the_child_scope()
    {
        using IContainer container = BuildContainer();

        TrackingDisposable tracker;
        await using (IAsyncTrackerEnv env = container.Resolve<IProcessingEnvBuilder>()
                   .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
                   .Configure(builder => builder.AddScoped<TrackingDisposable>())
                   .BuildAs<IAsyncTrackerEnv>())
        {
            tracker = env.Tracker;
            Assert.That(tracker.Disposed, Is.False);
        }

        Assert.That(tracker.Disposed, Is.True);
    }

    [Test]
    public void ThatDisposes_disposes_the_instance_with_the_scope()
    {
        using IContainer container = BuildContainer();
        TrackingDisposable owned = new();

        using (IEmptyEnv env = container.Resolve<IProcessingEnvBuilder>()
                   .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
                   .ThatDisposes(owned)
                   .BuildAs<IEmptyEnv>())
        {
            Assert.That(owned.Disposed, Is.False);
        }

        Assert.That(owned.Disposed, Is.True);
    }

    [Test]
    public void WithComponent_type_overload_registers_the_scoped_type()
    {
        using IContainer container = BuildContainer();

        using ITrackerEnv env = container.Resolve<IProcessingEnvBuilder>()
            .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
            .WithComponent<TrackingDisposable>()
            .BuildAs<ITrackerEnv>();

        Assert.That(env.Tracker, Is.Not.Null);
    }

    [Test]
    public void WithComponent_registers_but_does_not_dispose_the_instance()
    {
        using IContainer container = BuildContainer();
        TrackingDisposable component = new();

        using (ITrackerEnv env = container.Resolve<IProcessingEnvBuilder>()
                   .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
                   .WithComponent(component)
                   .BuildAs<ITrackerEnv>())
        {
            Assert.That(env.Tracker, Is.SameAs(component));
        }

        Assert.That(component.Disposed, Is.False);
    }

    [Test]
    public void WithOverridableEnv_auto_creates_the_env_and_resolves_its_components()
    {
        using IContainer container = BuildContainer();

        using IOverridableTestEnv env = container.Resolve<IProcessingEnvBuilder>()
            .WithOverridableEnv()
            .BuildAs<IOverridableTestEnv>();

        Assert.That(env.WorldState, Is.Not.Null);
        Assert.That(env.CodeInfoRepository, Is.Not.Null);
    }

    [Test]
    public async Task WithOverridableEnv_builds_the_world_state_scope_on_demand()
    {
        using IContainer container = BuildContainer();

        await using IOverridableWorldStateEnv env = container.Resolve<IProcessingEnvBuilder>()
            .WithOverridableEnv()
            .BuildAs<IOverridableWorldStateEnv>();

        // The no-arg overload must not open the world-state scope up front: were it eager, this on-demand
        // build would throw because the env's single scope would already be open.
        using Scope<IWorldState> scope = env.BuildAndOverride(null);
        Assert.That(scope.Component, Is.Not.Null);
    }

    [Test]
    public void BuildAs_supports_a_null_component_overridable_env_with_getters()
    {
        using IContainer container = BuildContainer();

        using INullComponentEnv env = container.Resolve<IProcessingEnvBuilder>()
            .WithOverridableEnv()
            .BuildAs<INullComponentEnv>();

        using (env.BuildAndOverride(null)) // Scope<Null>; only the scope lifetime matters
            Assert.That(env.WorldState, Is.Not.Null);
    }

    [Test]
    public async Task BuildAs_forwards_overridable_env_methods_and_disposes_the_scope()
    {
        using IContainer container = BuildContainer();
        IOverridableEnv overridableEnv = container.Resolve<IOverridableEnvFactory>().Create();

        IOverridableWorldStateEnv env = container.Resolve<IProcessingEnvBuilder>()
            .WithOverridableEnv(overridableEnv)
            .BuildAs<IOverridableWorldStateEnv>();

        using (Scope<IWorldState> scope = env.BuildAndOverride(null))
        {
            Assert.That(scope.Component, Is.Not.Null);
        }

        await env.DisposeAsync();
    }

    [Test]
    public void OwnedByParentLifetime_allows_non_disposable_wrapper_and_disposes_scope_with_parent()
    {
        using IContainer container = BuildContainer();

        // A non-disposable wrapper is rejected unless its scope is owned by the parent lifetime.
        Assert.That(
            () => container.Resolve<IProcessingEnvBuilder>()
                .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
                .BuildAs<IOwnedTrackerEnv>(),
            Throws.TypeOf<ArgumentException>());

        // Conversely, a disposable wrapper is rejected when owned by the parent lifetime.
        Assert.That(
            () => container.Resolve<IProcessingEnvBuilder>()
                .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
                .OwnedByParentLifetime()
                .BuildAs<IEmptyEnv>(),
            Throws.TypeOf<ArgumentException>());

        TrackingDisposable tracker;
        using (ILifetimeScope parent = container.BeginLifetimeScope())
        {
            IOwnedTrackerEnv env = parent.Resolve<IProcessingEnvBuilder>()
                .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
                .Configure(builder => builder.AddScoped<TrackingDisposable>())
                .OwnedByParentLifetime()
                .BuildAs<IOwnedTrackerEnv>();

            tracker = env.Tracker; // force construction inside the env scope
            Assert.That(tracker.Disposed, Is.False);
        }

        Assert.That(tracker.Disposed, Is.True); // the env scope was disposed together with the parent scope
    }

    [Test]
    public void Non_property_member_throws_during_build()
    {
        using IContainer container = BuildContainer();
        IProcessingEnvBuilder builder = container.Resolve<IProcessingEnvBuilder>()
            .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState());

        Assert.That(() => builder.BuildAs<IEnvWithMethod>(), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void BuildAs_without_world_state_throws()
    {
        using IContainer container = BuildContainer();
        IProcessingEnvBuilder builder = container.Resolve<IProcessingEnvBuilder>();

        Assert.That(() => builder.BuildAs<IEnvWithMethod>(), Throws.InstanceOf<InvalidOperationException>());
    }

    public sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
