// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class CompositeDiscoveryAppTests
{
    [Test]
    public async Task StopAsync_ShouldDisposeAsyncDiscoveryApps()
    {
        INetworkConfig networkConfig = Substitute.For<INetworkConfig>();
        networkConfig.LocalIp.Returns("127.0.0.1");
        DisposableDiscoveryApp discoveryApp = new();

        CompositeDiscoveryApp compositeDiscoveryApp = new(
            networkConfig,
            new DiscoveryConfig { DiscoveryVersion = DiscoveryVersion.V4 },
            LimboLogs.Instance,
            [discoveryApp]);

        await compositeDiscoveryApp.StopAsync();

        Assert.That(discoveryApp.StopCount, Is.EqualTo(1));
        Assert.That(discoveryApp.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public void StopAsync_ShouldContinueDisposingAsyncDiscoveryApps_WhenOneDisposeFails()
    {
        INetworkConfig networkConfig = Substitute.For<INetworkConfig>();
        networkConfig.LocalIp.Returns("127.0.0.1");
        DisposableDiscoveryApp throwingDiscoveryApp = new(throwOnDispose: true);
        DisposableDiscoveryApp discoveryApp = new();

        CompositeDiscoveryApp compositeDiscoveryApp = new(
            networkConfig,
            new DiscoveryConfig { DiscoveryVersion = DiscoveryVersion.V4 },
            LimboLogs.Instance,
            [throwingDiscoveryApp, discoveryApp]);

        Assert.That(async () => await compositeDiscoveryApp.StopAsync(), Throws.Nothing);
        Assert.That(throwingDiscoveryApp.DisposeCount, Is.EqualTo(1));
        Assert.That(discoveryApp.DisposeCount, Is.EqualTo(1));
    }

    private sealed class DisposableDiscoveryApp(bool throwOnDispose = false) : IDiscoveryApp, IAsyncDisposable
    {
        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void InitializeChannel(IChannel channel)
        {
        }

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void AddNodeToDiscovery(Node node)
        {
        }

        public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public event EventHandler<NodeEventArgs>? NodeRemoved
        {
            add { }
            remove { }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (throwOnDispose)
            {
                return ValueTask.FromException(new InvalidOperationException("Dispose failed"));
            }

            return ValueTask.CompletedTask;
        }
    }
}
