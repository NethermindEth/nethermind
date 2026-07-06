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
    public void Non_property_member_throws_NotSupported()
    {
        using IContainer container = BuildContainer();
        using IEnvWithMethod env = container.Resolve<IProcessingEnvBuilder>()
            .WithWorldState(container.Resolve<IWorldStateManager>().CreateResettableWorldState())
            .BuildAs<IEnvWithMethod>();

        Assert.That(env.DoSomething, Throws.TypeOf<NotSupportedException>());
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
