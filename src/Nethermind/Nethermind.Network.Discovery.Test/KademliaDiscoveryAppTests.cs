// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class KademliaDiscoveryAppTests
{
    [Test]
    public async Task DisposeAsync_StopsRunningDiscovery()
    {
        TestKademliaDiscoveryApp app = new();

        app.ActivateChannel();
        await app.StartAsync();
        await app.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await app.DisposeAsync();
        await app.DisposeAsync();

        Assert.That(app.Stopped.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(app.StopAsyncCoreCalls, Is.EqualTo(1));
        Assert.That(app.DisposeAsyncCoreCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task DisposeAsync_DisposesCore_WhenStopFails()
    {
        TestKademliaDiscoveryApp app = new(throwOnStop: true);

        app.ActivateChannel();
        await app.StartAsync();
        await app.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await app.DisposeAsync());

        Assert.That(exception?.Message, Is.EqualTo("Stop failed"));
        Assert.That(app.Stopped.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(app.StopAsyncCoreCalls, Is.EqualTo(1));
        Assert.That(app.DisposeAsyncCoreCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Activation_WaitsForInitializationAfterChannelActivates()
    {
        TestKademliaDiscoveryApp app = new(pauseInitialization: true);
        app.ActivateChannel();

        Task startTask = app.StartAsync();
        await app.InitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(app.Started.Task.IsCompleted, Is.False);

        app.AllowInitialization();
        await startTask;
        await app.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await app.DisposeAsync();
    }

    [Test]
    public async Task Activation_WaitsForChannelAfterInitializationCompletes()
    {
        TestKademliaDiscoveryApp app = new();

        await app.StartAsync();
        Assert.That(app.Started.Task.IsCompleted, Is.False);

        app.ActivateChannel();
        await app.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await app.DisposeAsync();
    }

    [Test]
    public async Task Activation_DoesNotStartAfterStop()
    {
        TestKademliaDiscoveryApp app = new();

        await app.StartAsync();
        await app.StopAsync();
        app.ActivateChannel();

        Assert.That(app.Started.Task.IsCompleted, Is.False);
        await app.DisposeAsync();
    }

    private sealed class TestKademliaDiscoveryApp(
        bool throwOnStop = false,
        bool pauseInitialization = false) : KademliaDiscoveryApp(
        "test discovery",
        new NetworkConfig { ExternalIp = "127.0.0.1" },
        new FixedIpResolver(new NetworkConfig { ExternalIp = "127.0.0.1" }),
        new ProcessExitSource(CancellationToken.None),
        LimboLogs.Instance.GetClassLogger<TestKademliaDiscoveryApp>())
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InitializationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource ContinueInitialization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopAsyncCoreCalls { get; private set; }

        public int DisposeAsyncCoreCalls { get; private set; }

        public void ActivateChannel() => OnChannelActivated(this, EventArgs.Empty);

        public void AllowInitialization() => ContinueInitialization.TrySetResult();

        public override void InitializeChannel(IChannel channel)
        {
        }

        protected override async Task Initialize(CancellationToken cancellationToken)
        {
            InitializationStarted.TrySetResult();
            if (pauseInitialization)
            {
                await ContinueInitialization.Task.WaitAsync(cancellationToken);
            }

            await base.Initialize(cancellationToken);
        }

        protected override async Task RunDiscoveryAsync(CancellationToken cancellationToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Stopped.SetResult();
            }
        }

        protected override Task StopAsyncCore()
        {
            StopAsyncCoreCalls++;
            if (throwOnStop)
            {
                throw new InvalidOperationException("Stop failed");
            }

            return Task.CompletedTask;
        }

        protected override ValueTask DisposeAsyncCore()
        {
            DisposeAsyncCoreCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
