// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.Model;

/// <summary>
/// Nethermind level disconnect reason. Don't forget to add the corresponding Eth level disconnect reason in `InitiateDisconnectReasonExtension`.
/// </summary>
public enum InitiateDisconnectReason : byte
{
    TooManyPeers,
    SessionAlreadyExist,
    ReplacingSessionWithOppositeDirection,
    OppositeDirectionCleanup,

    SnapServerNotImplemented,
    IncompatibleP2PVersion,
    InvalidNetworkId,
    InvalidGenesis,
    MissingForkId,
    InvalidForkId,
    ProtocolInitTimeout,
    TxFlooding,
    NoCapabilityMatched,

    UselessInFastBlocks,
    DropWorstPeer,
    PeerRefreshFailed,

    ForwardSyncFailed,
    GossipingInPoS,
    SessionIdAlreadyExists,
    AppClosing,

    // Sync related
    InvalidTxOrUncle,
    HeaderResponseTooLong,
    InconsistentHeaderBatch,
    UnexpectedHeaderHash,
    HeaderBatchOnDifferentBranch,
    UnexpectedParentHeader,
    InvalidHeader,
    InvalidReceiptRoot,

    // Try not to use this. Instead create a new one.
    Other,
}

public static class InitiateDisconnectReasonExtension
{
    public static DisconnectReason ToDisconnectReason(this InitiateDisconnectReason initiateDisconnectReason)
    {
        switch (initiateDisconnectReason)
        {
            case InitiateDisconnectReason.TooManyPeers:
                return DisconnectReason.TooManyPeers;
            case InitiateDisconnectReason.SessionAlreadyExist:
            case InitiateDisconnectReason.ReplacingSessionWithOppositeDirection:
            case InitiateDisconnectReason.OppositeDirectionCleanup:
                return DisconnectReason.AlreadyConnected;

            case InitiateDisconnectReason.SnapServerNotImplemented:
                return DisconnectReason.UselessPeer;
            case InitiateDisconnectReason.IncompatibleP2PVersion:
                return DisconnectReason.IncompatibleP2PVersion;
            case InitiateDisconnectReason.InvalidNetworkId:
                return DisconnectReason.UselessPeer;
            case InitiateDisconnectReason.InvalidGenesis:
            case InitiateDisconnectReason.MissingForkId:
            case InitiateDisconnectReason.InvalidForkId:
                return DisconnectReason.BreachOfProtocol;
            case InitiateDisconnectReason.ProtocolInitTimeout:
                return DisconnectReason.ReceiveMessageTimeout;
            case InitiateDisconnectReason.TxFlooding:
                return DisconnectReason.UselessPeer;
            case InitiateDisconnectReason.NoCapabilityMatched:
                return DisconnectReason.UselessPeer;

            case InitiateDisconnectReason.UselessInFastBlocks:
                return DisconnectReason.UselessPeer;
            case InitiateDisconnectReason.DropWorstPeer:
                return DisconnectReason.TooManyPeers;
            case InitiateDisconnectReason.PeerRefreshFailed:
                return DisconnectReason.DisconnectRequested;

            case InitiateDisconnectReason.ForwardSyncFailed:
                return DisconnectReason.DisconnectRequested;
            case InitiateDisconnectReason.GossipingInPoS:
                return DisconnectReason.BreachOfProtocol;
            case InitiateDisconnectReason.SessionIdAlreadyExists:
                return DisconnectReason.AlreadyConnected;
            case InitiateDisconnectReason.AppClosing:
                return DisconnectReason.ClientQuitting;

            case InitiateDisconnectReason.InvalidTxOrUncle:
            case InitiateDisconnectReason.HeaderResponseTooLong:
            case InitiateDisconnectReason.InconsistentHeaderBatch:
            case InitiateDisconnectReason.UnexpectedHeaderHash:
            case InitiateDisconnectReason.HeaderBatchOnDifferentBranch:
            case InitiateDisconnectReason.UnexpectedParentHeader:
            case InitiateDisconnectReason.InvalidHeader:
            case InitiateDisconnectReason.InvalidReceiptRoot:
                return DisconnectReason.BreachOfProtocol;
        }

        return DisconnectReason.Other;
    }
}
