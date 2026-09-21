// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Api;

/// <summary>
/// The driver services every endpoint group reads from. <see cref="P2P"/>, <see cref="PeerManager"/>
/// and <see cref="Discovery"/> are nullable: the sync orchestrator treats them the same way (see its
/// own optional constructor parameters) because they only exist once the driver has actually started
/// networking, and unit tests exercise the host without a live libp2p stack.
/// <see cref="ForkChoiceSnapshots"/> is nullable for the same reason on the test side; the container
/// always supplies it.
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
    ForkChoiceSnapshotHolder? ForkChoiceSnapshots = null);
