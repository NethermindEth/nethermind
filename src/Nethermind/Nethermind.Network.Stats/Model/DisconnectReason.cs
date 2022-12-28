// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.Model
{
    /// <summary>
    /// Eth network level disconnect reason
    /// </summary>
    public enum DisconnectReason : byte
    {
        DisconnectRequested = 0x00,
        TcpSubSystemError = 0x01,
        BreachOfProtocol = 0x02,
        UselessPeer = 0x03,
        TooManyPeers = 0x04,
        AlreadyConnected = 0x05,
        IncompatibleP2PVersion = 0x06,
        NullNodeIdentityReceived = 0x07,
        ClientQuitting = 0x08,
        UnexpectedIdentity = 0x09,
        IdentitySameAsSelf = 0x0a,
        ReceiveMessageTimeout = 0x0b,
        Other = 0x10,
        Breach1 = 0x11,
        Breach2 = 0x12,
        NdmInvalidHiSignature = 0x13,
        NdmHostAddressesNotConfigured = 0x14,
        NdmPeerAddressesNotConfigured = 0x15
    }

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
}
