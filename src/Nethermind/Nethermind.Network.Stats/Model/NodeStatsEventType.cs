// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.Model
{
    public enum NodeStatsEventType
    {
        DiscoveryPingOut,
        DiscoveryPingIn,
        DiscoveryPongOut,
        DiscoveryPongIn,
        DiscoveryNeighboursOut,
        DiscoveryNeighboursIn,
        DiscoveryFindNodeOut,
        DiscoveryFindNodeIn,
        DiscoveryEnrRequestOut,
        DiscoveryEnrRequestIn,
        DiscoveryEnrResponseOut,
        DiscoveryEnrResponseIn,

        P2PPingIn,
        P2PPingOut,

        NodeDiscovered,
        ConnectionEstablished,
        ConnectionFailedTargetUnreachable,
        ConnectionFailed,
        Connecting,
        HandshakeCompleted,
        P2PInitialized,
        Eth62Initialized,
        LesInitialized,
        SyncInitFailed,
        SyncInitCancelled,
        SyncInitCompleted,
        SyncStarted,
        SyncCancelled,
        SyncFailed,
        SyncCompleted,

        LocalDisconnectDelay,
        RemoteDisconnectDelay,

        Disconnect,

        None
    }
}
