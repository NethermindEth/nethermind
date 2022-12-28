// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.Model;

/// <summary>
/// Nethermind level disconnect reason. Don't forget to add the corresponding Eth level disconnect reason in `InitiateDisconnectReasonExtension`.
/// </summary>
public enum InitiateDisconnectReason : byte
{
    IncomingConnectionRejectedTooManyPeer,
    SessionAlreadyExist,
    LostSessionDirectionDecision,
    OppositeDirectionCleanup,

    SnapServerNotImplemented,
    IncompatibleP2PVersion,
    InvalidCapability,
    InvalidChainId,
    InvalidGenesis,
    ProtocolInitTimeout,
    TxFlooding,
    NoCapabilityMatched,

    SyncPeerPoolBreachOfProtocol,
    SyncPeerPoolUselessPeer,
    SyncPeerPoolDropWorstPeer,
    SyncPeerPoolRefreshFailed,

    ForwardSyncFailed,
    PoSDisconnectGossipingPeer,
    SyncPeerAddFailed,
    AppClosing,

    // Try not to use this. Instead crease a new one.
    Other
}

public static class InitiateDisconnectReasonExtension
{
    public static DisconnectReason ToDisconnectReason(this InitiateDisconnectReason initiateDisconnectReason)
    {
        switch (initiateDisconnectReason)
        {
            case InitiateDisconnectReason.IncomingConnectionRejectedTooManyPeer:
                return DisconnectReason.TooManyPeers;
            case InitiateDisconnectReason.SessionAlreadyExist:
            case InitiateDisconnectReason.LostSessionDirectionDecision:
            case InitiateDisconnectReason.OppositeDirectionCleanup:
                return DisconnectReason.AlreadyConnected;

            case InitiateDisconnectReason.SnapServerNotImplemented:
                return DisconnectReason.UselessPeer;
            case InitiateDisconnectReason.IncompatibleP2PVersion:
                return DisconnectReason.IncompatibleP2PVersion;
            case InitiateDisconnectReason.InvalidCapability:
                return DisconnectReason.UselessPeer;
            case InitiateDisconnectReason.InvalidChainId:
                return DisconnectReason.UselessPeer;
            case InitiateDisconnectReason.InvalidGenesis:
                return DisconnectReason.BreachOfProtocol;
            case InitiateDisconnectReason.ProtocolInitTimeout:
                return DisconnectReason.ReceiveMessageTimeout;
            case InitiateDisconnectReason.TxFlooding:
                return DisconnectReason.UselessPeer;
            case InitiateDisconnectReason.NoCapabilityMatched:
                return DisconnectReason.UselessPeer;

            case InitiateDisconnectReason.SyncPeerPoolBreachOfProtocol:
                return DisconnectReason.BreachOfProtocol;
            case InitiateDisconnectReason.SyncPeerPoolUselessPeer:
                return DisconnectReason.UselessPeer;
            case InitiateDisconnectReason.SyncPeerPoolDropWorstPeer:
                return DisconnectReason.TooManyPeers;
            case InitiateDisconnectReason.SyncPeerPoolRefreshFailed:
                return DisconnectReason.DisconnectRequested;

            case InitiateDisconnectReason.ForwardSyncFailed:
                return DisconnectReason.DisconnectRequested;
            case InitiateDisconnectReason.PoSDisconnectGossipingPeer:
                return DisconnectReason.BreachOfProtocol;
            case InitiateDisconnectReason.SyncPeerAddFailed:
                return DisconnectReason.AlreadyConnected;
            case InitiateDisconnectReason.AppClosing:
                return DisconnectReason.ClientQuitting;
        }

        return DisconnectReason.Other;
    }
}
