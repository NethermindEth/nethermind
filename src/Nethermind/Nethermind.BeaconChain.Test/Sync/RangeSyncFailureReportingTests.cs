// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Test.Sync;

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
        RangeSyncTests.StubPeer badPeer = CreatePeer("bad", headSlot: TargetSlot + 1, (startSlot, count) => behavior switch
        {
            BadPeerBehavior.RequestTimesOut => throw new TimeoutException("request timed out"),
            BadPeerBehavior.SessionIsGone => throw new IOException("Channel closed"),
            BadPeerBehavior.SessionIsGoneAfterABlock => throw new PartialBlocksException(
                new IOException("peer disconnected after 1 s: its libp2p session closed", new OperationCanceledException()),
                [new ForkedSignedBeaconBlock.OfFulu(chain.First(b => b.Message!.Slot >= startSlot))]),
            _ => [TestChain.CreateBlock(startSlot, parentRoot: Hash256.Zero), .. chain.Skip(1)],
        });
        RangeSyncTests.StubPeer goodPeer = CreatePeer("good", headSlot: TargetSlot, (startSlot, count) => [.. chain.Where(b => b.Message!.Slot >= startSlot && b.Message.Slot < startSlot + count)]);
        RangeSync sync = new(new RangeSyncTests.StubPool(badPeer, goodPeer), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, RangeSyncTests.ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> imported = await RangeSyncTests.CollectAsync(sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(imported.Select(b => b.Slot), Is.EqualTo(chain.Select(b => b.Message!.Slot)), "the good peer still completes the range");
        Assert.That(badPeer.Reports, Is.Not.Empty.And.All.EqualTo(expected), "the bad peer is penalized under the reason that describes what it did");
        Assert.That(goodPeer.Reports, Is.Empty);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_reply_that_fails_after_some_blocks_keeps_them_and_only_the_rest_is_requested(CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 11, 12);
        List<(ulong Start, ulong Count)> requests = [];
        RangeSyncTests.StubPeer peer = CreatePeer("cut-short", headSlot: TargetSlot, (startSlot, count) =>
        {
            requests.Add((startSlot, count));
            SignedBeaconBlock[] served = [.. chain.Where(b => b.Message!.Slot >= startSlot && b.Message.Slot < startSlot + count)];
            return requests.Count > 1 ? served : throw new PartialBlocksException(new TimeoutException("request timed out"), [new ForkedSignedBeaconBlock.OfFulu(served[0])]);
        });
        RangeSync sync = new(new RangeSyncTests.StubPool(peer), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, RangeSyncTests.ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> imported = await RangeSyncTests.CollectAsync(sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(imported.Select(static b => b.Slot), Is.EqualTo(chain.Select(static b => b.Message!.Slot)));
        Assert.That(requests, Is.EqualTo(new[] { (11UL, 2UL), (12UL, 1UL) }), "the retry starts after the kept block");
        Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.RequestFailed }));
    }

    private static RangeSyncTests.StubPeer CreatePeer(string id, ulong headSlot, Func<ulong, ulong, SignedBeaconBlock[]> handler) =>
        new(id, headSlot, (startSlot, count) => [.. handler(startSlot, count).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))],
            custody: PeerColumnCustody.None, rootHandler: static _ => throw new NotSupportedException());
}
