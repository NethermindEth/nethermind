// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.BeaconChain.P2P;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.ReqResp;

public class RequestViolationWiringTests
{
    // Without the pool the host cannot find the peer that broke the protocol, and its violations go unrecorded.
    [Test]
    public void The_host_reports_request_violations_to_the_pool_peer_selection_uses()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();

        Assert.That(container.Resolve<BeaconP2P>().PeerPoolForTest, Is.SameAs(container.Resolve<IBeaconSyncPeerPool>()));
    }
}
