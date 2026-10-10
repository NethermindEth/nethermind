// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Init.Steps;
using Nethermind.Network;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Init.Test;

[TestFixture]
public class InitializeNetworkTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void StartPeer_respects_peer_manager_enabled(bool peerManagerEnabled)
    {
        // Targeted overrides: the only collaborators we need to observe are the peer services.
        // Everything else is wired by the production modules (TestNethermindModule -> NethermindModule
        // -> BuiltInStepsModule registers InitializeNetwork as a step).
        IPeerPool peerPool = Substitute.For<IPeerPool>();
        IPeerManager peerManager = Substitute.For<IPeerManager>();
        ISessionMonitor sessionMonitor = Substitute.For<ISessionMonitor>();

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new InitConfig { PeerManagerEnabled = peerManagerEnabled }))
            .AddSingleton(peerPool)
            .AddSingleton(peerManager)
            .AddSingleton(sessionMonitor)
            .Build();

        // Resolve through production DI so the constructor runs in full and every field is set by the container,
        // instead of bypassing the constructor with RuntimeHelpers.GetUninitializedObject.
        InitializeNetwork network = container.Resolve<InitializeNetwork>();

        InvokeStartPeer(network);

        if (peerManagerEnabled)
        {
            peerPool.Received().Start();
            peerManager.Received().Start();
            sessionMonitor.Received().Start();
        }
        else
        {
            peerPool.DidNotReceive().Start();
            peerManager.DidNotReceive().Start();
            sessionMonitor.DidNotReceive().Start();
        }
    }

    private static void InvokeStartPeer(InitializeNetwork instance) =>
        typeof(InitializeNetwork)
            .GetMethod("StartPeer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(instance, null);
}
