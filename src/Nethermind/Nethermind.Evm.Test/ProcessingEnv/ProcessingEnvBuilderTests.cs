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

        using ITestProcessingEnv env = container.Resolve<IProcessingEnvBuilder>().NewEnv()
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
        using (ITrackerEnv env = container.Resolve<IProcessingEnvBuilder>().NewEnv()
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
        await using (IAsyncTrackerEnv env = container.Resolve<IProcessingEnvBuilder>().NewEnv()
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

        using (IEmptyEnv env = container.Resolve<IProcessingEnvBuilder>().NewEnv()
                   .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
                   .ThatDisposes(owned)
                   .BuildAs<IEmptyEnv>())
        {
            Assert.That(owned.Disposed, Is.False);
        }

        Assert.That(owned.Disposed, Is.True);
    }

    [Test]
    public void WithComponent_registers_but_does_not_dispose_the_instance()
    {
        using IContainer container = BuildContainer();
        TrackingDisposable component = new();

        using (ITrackerEnv env = container.Resolve<IProcessingEnvBuilder>().NewEnv()
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

        using IOverridableTestEnv env = container.Resolve<IProcessingEnvBuilder>().NewEnv()
            .WithOverridableEnv()
            .BuildAs<IOverridableTestEnv>();

        Assert.That(env.WorldState, Is.Not.Null);
        Assert.That(env.CodeInfoRepository, Is.Not.Null);
    }

    [Test]
    public void Non_property_member_throws_during_build()
    {
        using IContainer container = BuildContainer();
        IProcessingEnvBuilder.IDsl builder = container.Resolve<IProcessingEnvBuilder>().NewEnv()
            .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState());

        Assert.That(() => builder.BuildAs<IEnvWithMethod>(), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void BuildAs_without_world_state_throws()
    {
        using IContainer container = BuildContainer();
        IProcessingEnvBuilder.IDsl builder = container.Resolve<IProcessingEnvBuilder>().NewEnv();

        Assert.That(() => builder.BuildAs<IEnvWithMethod>(), Throws.InstanceOf<InvalidOperationException>());
    }

    public sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
