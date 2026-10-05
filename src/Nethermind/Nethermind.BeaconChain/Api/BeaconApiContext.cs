// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.RateLimiting;
using Nethermind.BeaconChain.Api.Endpoints;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Api;

/// <summary>
/// The driver services every endpoint group reads from. <see cref="P2P"/>, <see cref="PeerManager"/>
/// and <see cref="Discovery"/> are nullable: the sync orchestrator treats them the same way (see its
/// own optional constructor parameters) because they only exist once the driver has actually started
/// networking, and unit tests exercise the host without a live libp2p stack.
/// <see cref="ForkChoiceSnapshots"/> and <see cref="HeadSnapshots"/> are nullable for the same reason on the test
/// side; the container always supplies them.
/// </summary>
internal sealed record BeaconApiContext(
    IBeaconChainConfig ChainConfig,
    BeaconChainSpec Spec,
    IBeaconChainStatusSource StatusSource,
    SlotClock SlotClock,
    BeaconChainStore Store,
    LocalMetadataSource MetadataSource,
    IEngineDriver Engine,
    ILogManager LogManager,
    BeaconP2P? P2P,
    PeerManager? PeerManager,
    BeaconDiscovery? Discovery,
    ForkChoiceSnapshotHolder? ForkChoiceSnapshots = null,
    HeadSnapshotHolder? HeadSnapshots = null,
    DataColumnSidecarPool? ColumnPool = null)
{
    /// <summary>Captures one get_head view for all reads in a request (fork-choice.md).</summary>
    public BeaconApiContext ForRequest() => this with { StatusSource = CaptureHead(), ForkChoiceSnapshot = ForkChoiceSnapshots?.Current };
    public ForkChoiceSnapshot? ForkChoiceSnapshot { get; private init; }
    /// <summary>Bounds the blob rebuilds of this host's getBlobs requests that run at once; a request past it is refused, never queued.</summary>
    public ConcurrencyLimiter BlobRebuilds { get; } = new(new ConcurrencyLimiterOptions { PermitLimit = BlobsEndpoint.MaxConcurrentRebuilds, QueueLimit = 0 });

    /// <summary>Freezes the published get_head view, or the startup status before publication (fork-choice.md).</summary>
    public IBeaconChainStatusSource CaptureHead()
    {
        if (HeadSnapshots?.Current is { } snapshot)
        {
            return new FrozenHead(snapshot);
        }

        (StatusMessageV2 status, Hash256? fullHeadRoot) = StatusSource.CurrentHead;
        return new FrozenHead(new HeadSnapshot(status, fullHeadRoot, StatusSource.JustifiedRoot, StatusSource.ExecutionInSync));
    }

    private sealed class FrozenHead(HeadSnapshot snapshot) : IBeaconChainStatusSource
    {
        public StatusMessageV2 CurrentStatus => snapshot.Status;
        public Hash256 JustifiedRoot => snapshot.JustifiedRoot;
        public bool ExecutionInSync => snapshot.ExecutionInSync;
        public (StatusMessageV2 Status, Hash256? FullHeadRoot) CurrentHead => (snapshot.Status, snapshot.FullHeadRoot);
    }
}
