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
    public static EthDisconnectReason ToDisconnectReason(this InitiateDisconnectReason initiateDisconnectReason)
    {
        switch (initiateDisconnectReason)
        {
            case InitiateDisconnectReason.TooManyPeers:
                return EthDisconnectReason.TooManyPeers;
            case InitiateDisconnectReason.SessionAlreadyExist:
            case InitiateDisconnectReason.ReplacingSessionWithOppositeDirection:
            case InitiateDisconnectReason.OppositeDirectionCleanup:
                return EthDisconnectReason.AlreadyConnected;

            case InitiateDisconnectReason.SnapServerNotImplemented:
                return EthDisconnectReason.UselessPeer;
            case InitiateDisconnectReason.IncompatibleP2PVersion:
                return EthDisconnectReason.IncompatibleP2PVersion;
            case InitiateDisconnectReason.InvalidNetworkId:
                return EthDisconnectReason.UselessPeer;
            case InitiateDisconnectReason.InvalidGenesis:
            case InitiateDisconnectReason.MissingForkId:
            case InitiateDisconnectReason.InvalidForkId:
                return EthDisconnectReason.BreachOfProtocol;
            case InitiateDisconnectReason.ProtocolInitTimeout:
                return EthDisconnectReason.ReceiveMessageTimeout;
            case InitiateDisconnectReason.TxFlooding:
                return EthDisconnectReason.UselessPeer;
            case InitiateDisconnectReason.NoCapabilityMatched:
                return EthDisconnectReason.UselessPeer;

            case InitiateDisconnectReason.DropWorstPeer:
                return EthDisconnectReason.TooManyPeers;
            case InitiateDisconnectReason.PeerRefreshFailed:
                return EthDisconnectReason.DisconnectRequested;

            case InitiateDisconnectReason.ForwardSyncFailed:
                return EthDisconnectReason.DisconnectRequested;
            case InitiateDisconnectReason.GossipingInPoS:
                return EthDisconnectReason.BreachOfProtocol;
            case InitiateDisconnectReason.SessionIdAlreadyExists:
                return EthDisconnectReason.AlreadyConnected;
            case InitiateDisconnectReason.AppClosing:
                return EthDisconnectReason.ClientQuitting;

            case InitiateDisconnectReason.InvalidTxOrUncle:
            case InitiateDisconnectReason.HeaderResponseTooLong:
            case InitiateDisconnectReason.InconsistentHeaderBatch:
            case InitiateDisconnectReason.UnexpectedHeaderHash:
            case InitiateDisconnectReason.HeaderBatchOnDifferentBranch:
            case InitiateDisconnectReason.UnexpectedParentHeader:
            case InitiateDisconnectReason.InvalidHeader:
            case InitiateDisconnectReason.InvalidReceiptRoot:
                return EthDisconnectReason.BreachOfProtocol;
        }

        return EthDisconnectReason.Other;
    }
}
