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
using Nethermind.BeaconChain.Spec;
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
    }

    [TestCase(BadPeerBehavior.WrongParentBatch, PeerFailureReason.ProtocolViolation)]
    [TestCase(BadPeerBehavior.RequestTimesOut, PeerFailureReason.RequestFailed)]
    [TestCase(BadPeerBehavior.SessionIsGone, PeerFailureReason.SessionClosed)]
    [CancelAfter(30_000)]
    public async Task Every_failure_is_reported_under_the_reason_that_describes_it(BadPeerBehavior behavior, PeerFailureReason expected, CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 11, 12);
        StubPeer badPeer = new("bad", headSlot: TargetSlot + 1, (startSlot, count) => behavior switch
        {
            BadPeerBehavior.RequestTimesOut => throw new TimeoutException("request timed out"),
            BadPeerBehavior.SessionIsGone => throw new IOException("Channel closed"),
            _ => [TestChain.CreateBlock(startSlot, parentRoot: Hash256.Zero), .. chain.Skip(1)],
        });
        StubPeer goodPeer = new("good", headSlot: TargetSlot, (startSlot, count) => [.. chain.Where(b => b.Message!.Slot >= startSlot && b.Message.Slot < startSlot + count)]);
        RangeSync sync = new(new StubPool(badPeer, goodPeer), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet);

        List<SignedBeaconBlock> imported = [];
        await foreach (SignedBeaconBlock block in sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, token))
        {
            imported.Add(block);
        }

        Assert.Multiple(() =>
        {
            Assert.That(imported.Select(b => b.Message!.Slot), Is.EqualTo(chain.Select(b => b.Message!.Slot)), "the good peer still completes the range");
            Assert.That(badPeer.TypedReports, Is.Not.Empty.And.All.EqualTo(expected), "the bad peer is penalized under the reason that describes what it did");
            Assert.That(goodPeer.TypedReports, Is.Empty);
        });
    }

    private sealed class StubPeer(string id, ulong headSlot, Func<ulong, ulong, SignedBeaconBlock[]> handler) : IBeaconSyncPeer
    {
        public List<PeerFailureReason> TypedReports { get; } = [];

        public string Id => id;

        public ulong HeadSlot => headSlot;

        public Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SignedBeaconBlock>>(handler(startSlot, count));

        public Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SignedBeaconBlock>>([]);

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DataColumnSidecar>>([]);

        public void ReportFailure(PeerFailureReason reason, string? detail = null) => TypedReports.Add(reason);
    }

    private sealed class StubPool(params IBeaconSyncPeer[] peers) : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) => [.. peers.Where(p => p.HeadSlot >= minHeadSlot)];
    }
}
