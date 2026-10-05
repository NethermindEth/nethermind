// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.UPnP.Plugin;
using NSubstitute;
using NUnit.Framework;
using SharpOpenNat;

namespace Nethermind.Runner.Test;

public class UPnPStepTests
{
    [Test]
    public async Task Maps_ports_with_lease_duration_in_seconds()
    {
        List<Mapping> mappings = [];
        TaskCompletionSource bothMapped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        INatDevice device = Substitute.For<INatDevice>();
        device.CreatePortMapAsync(default!, default).ReturnsForAnyArgs(call =>
        {
            mappings.Add(call.Arg<Mapping>());
            if (mappings.Count == 2) bothMapped.TrySetResult();
            return Task.CompletedTask;
        });

        INatDiscoverer discoverer = Substitute.For<INatDiscoverer>();
        discoverer.DiscoverDeviceAsync(PortMapper.Upnp, Arg.Any<CancellationToken>()).Returns(device);

        using CancellationTokenSource exitSource = new();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(exitSource.Token);

        NetworkConfig networkConfig = new() { P2PPort = 30303, DiscoveryPort = 30304 };

        await using UPnPStep step = new(discoverer, processExitSource, networkConfig, LimboLogs.Instance);
        await step.Execute(CancellationToken.None);
        await bothMapped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        exitSource.Cancel();

        int leaseSeconds = (int)TimeSpan.FromMinutes(30).TotalSeconds;
        Assert.That(mappings.Select(static m => (m.Protocol, m.PrivatePort, m.PublicPort, m.Lifetime)), Is.EquivalentTo(new[]
        {
            (Protocol.Tcp, 30303, 30303, leaseSeconds),
            (Protocol.Udp, 30304, 30304, leaseSeconds)
        }));
    }
}
