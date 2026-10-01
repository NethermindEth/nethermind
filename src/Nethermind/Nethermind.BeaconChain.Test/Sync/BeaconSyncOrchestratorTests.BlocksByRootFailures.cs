// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>A failed parent fetch by root reports the peer under the reason its failure maps to, so a dead session is dropped at once.</summary>
public partial class BeaconSyncOrchestratorTests
{
    [TestCase("Channel closed", PeerFailureReason.SessionClosed)]
    [TestCase("Peer responded with error code 2: oops", PeerFailureReason.RequestFailed)]
    public async Task A_failed_parent_fetch_by_root_reports_the_peer_under_the_classified_reason(string failure, PeerFailureReason expected)
    {
        (IBeaconSyncPeer peer, Harness harness, ForkedSignedBeaconBlock child) = CreateUnknownParentScenario();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ForkedSignedBeaconBlock>>(new Exception(failure)));

        await harness.Orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);

        peer.Received(1).ReportFailure(expected, Arg.Is<string?>(d => d != null && d.Contains(failure)));
    }

    [Test]
    public async Task A_parent_fetch_by_root_ended_by_the_callers_cancellation_reports_nothing()
    {
        (IBeaconSyncPeer peer, Harness harness, ForkedSignedBeaconBlock child) = CreateUnknownParentScenario();
        using CancellationTokenSource caller = new();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            caller.Cancel();
            return Task.FromException<IReadOnlyList<ForkedSignedBeaconBlock>>(new OperationCanceledException(caller.Token));
        });

        await harness.Orchestrator.ProcessGossipBlockAsync(child, caller.Token);

        peer.DidNotReceiveWithAnyArgs().ReportFailure(default);
        Assert.That(harness.Orchestrator.QueuedWorkCount, Is.Zero, "a fetch ended by shutdown reports no result to the worker");
    }

    private static (IBeaconSyncPeer Peer, Harness Harness, ForkedSignedBeaconBlock Child) CreateUnknownParentScenario()
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1, NearWallSlot + 2);
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        return (peer, harness, new ForkedSignedBeaconBlock.OfFulu(chain[1]));
    }
}
