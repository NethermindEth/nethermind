// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// <see cref="RangeSync"/> reports every peer failure under the <see cref="PeerFailureReason"/> that describes
/// what the peer did. The peer manager's fatal-session fast path keys off <see cref="PeerFailureReason.SessionClosed"/>,
/// so a misclassified dead session would stay on a failure budget it can never work off.
/// </summary>
public class RangeSyncFailureReportingTests
{
    private const ulong AnchorSlot = 10;
    private const ulong TargetSlot = 12;

    public enum BadPeerBehavior
    {
        WrongParentBatch,
        RequestTimesOut,
        SessionIsGone,
        SessionIsGoneAfterABlock,
    }

    [TestCase(BadPeerBehavior.WrongParentBatch, PeerFailureReason.ProtocolViolation)]
    [TestCase(BadPeerBehavior.RequestTimesOut, PeerFailureReason.RequestFailed)]
    [TestCase(BadPeerBehavior.SessionIsGone, PeerFailureReason.SessionClosed)]
    [TestCase(BadPeerBehavior.SessionIsGoneAfterABlock, PeerFailureReason.SessionClosed)]
    [CancelAfter(30_000)]
    public async Task Every_failure_is_reported_under_the_reason_that_describes_it(BadPeerBehavior behavior, PeerFailureReason expected, CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 11, 12);
        StubPeer badPeer = new("bad", headSlot: TargetSlot + 1, (startSlot, count) => behavior switch
        {
            BadPeerBehavior.RequestTimesOut => throw new TimeoutException("request timed out"),
            BadPeerBehavior.SessionIsGone => throw new IOException("Channel closed"),
            BadPeerBehavior.SessionIsGoneAfterABlock => throw new PartialBlocksException(
                new IOException("peer disconnected after 1 s: its libp2p session closed", new OperationCanceledException()),
                [new ForkedSignedBeaconBlock.OfFulu(chain.First(b => b.Message!.Slot >= startSlot))]),
            _ => [TestChain.CreateBlock(startSlot, parentRoot: Hash256.Zero), .. chain.Skip(1)],
        });
        StubPeer goodPeer = new("good", headSlot: TargetSlot, (startSlot, count) => [.. chain.Where(b => b.Message!.Slot >= startSlot && b.Message.Slot < startSlot + count)]);
        RangeSync sync = new(new StubPool(badPeer, goodPeer), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, RangeSyncTests.ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> imported = [];
        await foreach (ForkedSignedBeaconBlock block in sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, token))
        {
            imported.Add(block);
        }

        Assert.Multiple(() =>
        {
            Assert.That(imported.Select(b => b.Slot), Is.EqualTo(chain.Select(b => b.Message!.Slot)), "the good peer still completes the range");
            Assert.That(badPeer.TypedReports, Is.Not.Empty.And.All.EqualTo(expected), "the bad peer is penalized under the reason that describes what it did");
            Assert.That(goodPeer.TypedReports, Is.Empty);
        });
    }

    /// <summary>A reply that fails after some blocks used to drop them, so the batch was fetched again from its first slot; the blocks it delivered are kept and the peer is still penalized.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_reply_that_fails_after_some_blocks_keeps_them_and_only_the_rest_is_requested(CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 11, 12);
        List<(ulong Start, ulong Count)> requests = [];
        StubPeer peer = new("cut-short", headSlot: TargetSlot, (startSlot, count) =>
        {
            requests.Add((startSlot, count));
            SignedBeaconBlock[] served = [.. chain.Where(b => b.Message!.Slot >= startSlot && b.Message.Slot < startSlot + count)];
            return requests.Count > 1 ? served : throw new PartialBlocksException(new TimeoutException("request timed out"), [new ForkedSignedBeaconBlock.OfFulu(served[0])]);
        });
        RangeSync sync = new(new StubPool(peer), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, RangeSyncTests.ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> imported = [];
        await foreach (ForkedSignedBeaconBlock block in sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, token))
        {
            imported.Add(block);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.Select(static b => b.Slot), Is.EqualTo(chain.Select(static b => b.Message!.Slot)));
            Assert.That(requests, Is.EqualTo(new[] { (11UL, 2UL), (12UL, 1UL) }), "the retry starts after the kept block");
            Assert.That(peer.TypedReports, Is.EqualTo(new[] { PeerFailureReason.RequestFailed }));
        }
    }

    private sealed class StubPeer(string id, ulong headSlot, Func<ulong, ulong, SignedBeaconBlock[]> handler) : IBeaconSyncPeer
    {
        public List<PeerFailureReason> TypedReports { get; } = [];

        public string Id => id;

        public ulong HeadSlot => headSlot;

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([.. handler(startSlot, count).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))]);

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([]);

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DataColumnSidecar>>([]);

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token) =>
            throw new NotSupportedException();

        public PeerColumnCustody Custody => PeerColumnCustody.None;

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong startSlot, ulong count, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] roots, CancellationToken token) =>
            throw new NotSupportedException();

        public void ReportFailure(PeerFailureReason reason, string? detail = null) => TypedReports.Add(reason);
    }

    private sealed class StubPool(params IBeaconSyncPeer[] peers) : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) => [.. peers.Where(p => p.HeadSlot >= minHeadSlot)];
    }
}
