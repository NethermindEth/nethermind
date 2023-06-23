// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.Model;

/// <summary>
/// Nethermind level disconnect reason. Don't forget to add the corresponding Eth level disconnect reason in `InitiateDisconnectReasonExtension`.
/// </summary>
public enum DisconnectReason : byte
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
    public static EthDisconnectReason ToEthDisconnectReason(this DisconnectReason disconnectReason)
    {
        switch (disconnectReason)
        {
            case DisconnectReason.TooManyPeers:
                return EthDisconnectReason.TooManyPeers;
            case DisconnectReason.SessionAlreadyExist:
            case DisconnectReason.ReplacingSessionWithOppositeDirection:
            case DisconnectReason.OppositeDirectionCleanup:
                return EthDisconnectReason.AlreadyConnected;

            case DisconnectReason.SnapServerNotImplemented:
                return EthDisconnectReason.UselessPeer;
            case DisconnectReason.IncompatibleP2PVersion:
                return EthDisconnectReason.IncompatibleP2PVersion;
            case DisconnectReason.InvalidNetworkId:
                return EthDisconnectReason.UselessPeer;
            case DisconnectReason.InvalidGenesis:
            case DisconnectReason.MissingForkId:
            case DisconnectReason.InvalidForkId:
                return EthDisconnectReason.BreachOfProtocol;
            case DisconnectReason.ProtocolInitTimeout:
                return EthDisconnectReason.ReceiveMessageTimeout;
            case DisconnectReason.TxFlooding:
                return EthDisconnectReason.UselessPeer;
            case DisconnectReason.NoCapabilityMatched:
                return EthDisconnectReason.UselessPeer;

            case DisconnectReason.DropWorstPeer:
                return EthDisconnectReason.TooManyPeers;
            case DisconnectReason.PeerRefreshFailed:
                return EthDisconnectReason.DisconnectRequested;

            case DisconnectReason.ForwardSyncFailed:
                return EthDisconnectReason.DisconnectRequested;
            case DisconnectReason.GossipingInPoS:
                return EthDisconnectReason.BreachOfProtocol;
            case DisconnectReason.SessionIdAlreadyExists:
                return EthDisconnectReason.AlreadyConnected;
            case DisconnectReason.AppClosing:
                return EthDisconnectReason.ClientQuitting;

            case DisconnectReason.InvalidTxOrUncle:
            case DisconnectReason.HeaderResponseTooLong:
            case DisconnectReason.InconsistentHeaderBatch:
            case DisconnectReason.UnexpectedHeaderHash:
            case DisconnectReason.HeaderBatchOnDifferentBranch:
            case DisconnectReason.UnexpectedParentHeader:
            case DisconnectReason.InvalidHeader:
            case DisconnectReason.InvalidReceiptRoot:
                return EthDisconnectReason.BreachOfProtocol;
        }

        return EthDisconnectReason.Other;
    }
}
