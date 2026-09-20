// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>
/// The client string the Beacon API's <c>node/version</c> and <c>node/identity</c> endpoints must
/// report - the same one the libp2p Identify protocol advertises, so a caller sees one client
/// identity rather than two that could drift apart.
/// </summary>
/// <remarks>
/// <see cref="Nethermind.BeaconChain.P2P.BeaconP2P"/> builds this exact string inline
/// (<c>$"nethermind/{ProductInfo.Version}"</c>) for its <c>IdentifyProtocolSettings.AgentVersion</c>,
/// but does not expose it as a member, and <c>P2P/</c> is off limits for this change (owned by
/// another agent in this fold-back). This recomputes the same expression from the same
/// <see cref="ProductInfo.Version"/> source rather than a second hardcoded literal, and
/// <c>ClientIdentityTests.AgentVersion_matches_the_literal_BeaconP2P_builds</c> pins the two
/// call sites' output equal so a future edit to either format string fails the build instead of
/// silently diverging. The orchestrator should prefer exposing a
/// <c>BeaconP2P.ClientAgentVersion</c> property instead - see the report for the exact diff.
/// </remarks>
internal static class ClientIdentity
{
    public static string AgentVersion => $"nethermind/{ProductInfo.Version}";
}
