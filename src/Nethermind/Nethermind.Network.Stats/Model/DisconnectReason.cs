// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.Model;

/// <summary>
/// Nethermind level disconnect reason. This is different than `EthDisconnectReason` as its more detailed, specific
/// to Nethermind and does not map 1-1 with ethereum disconnect message code. Useful for metrics, debugging and
/// peer disconnect delays.
/// Don't forget to add the corresponding Eth level disconnect reason in `DisconnectReasonExtension`.
/// </summary>
public enum DisconnectReason : byte
{
    // Connection related
    SessionIdAlreadyExists,
    ConnectionClosed,
    OutgoingConnectionFailed,
    DuplicatedConnection,
    PeerRemoved,
    TooManyPeers,
    SessionAlreadyExist,
    ReplacingSessionWithOppositeDirection,
    OppositeDirectionCleanup,

    // Non sync, non connection related disconnect
    SnapServerNotImplemented,
    IncompatibleP2PVersion,
    InvalidNetworkId,
    InvalidGenesis,
    MissingForkId,
    InvalidForkId,
    ProtocolInitTimeout,
    TxFlooding,
    NoCapabilityMatched,
    ClientFiltered,
    AppClosing,
    DropWorstPeer,
    PeerRefreshFailed,
    GossipingInPoS,

    // Sync related
    ForwardSyncFailed,
    InvalidTxOrUncle,
    HeaderResponseTooLong,
    InconsistentHeaderBatch,
    UnexpectedHeaderHash,
    HeaderBatchOnDifferentBranch,
    UnexpectedParentHeader,
    InvalidHeader,
    InvalidReceiptRoot,

    // These are from EthDisconnectReason which does not necessarily used in Nethermind.
    EthDisconnectRequested,
    TcpSubSystemError,
    BreachOfProtocol,
    UselessPeer,
    AlreadyConnected,
    NullNodeIdentityReceived,
    ClientQuitting,
    UnexpectedIdentity,
    IdentitySameAsSelf,
    ReceiveMessageTimeout,

    // Try not to use this. Instead create a new one.
    Other,
}

public static class DisconnectReasonExtension
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
            case DisconnectReason.DuplicatedConnection:
            case DisconnectReason.SessionIdAlreadyExists:
                return EthDisconnectReason.AlreadyConnected;

            case DisconnectReason.ConnectionClosed:
            case DisconnectReason.OutgoingConnectionFailed:
                return EthDisconnectReason.TcpSubSystemError;

            case DisconnectReason.IncompatibleP2PVersion:
                return EthDisconnectReason.IncompatibleP2PVersion;

            case DisconnectReason.InvalidNetworkId:
            case DisconnectReason.InvalidGenesis:
            case DisconnectReason.MissingForkId:
            case DisconnectReason.InvalidForkId:
                return EthDisconnectReason.BreachOfProtocol;
            case DisconnectReason.ClientFiltered:
                return EthDisconnectReason.DisconnectRequested;

            case DisconnectReason.ProtocolInitTimeout:
                return EthDisconnectReason.ReceiveMessageTimeout;

            case DisconnectReason.SnapServerNotImplemented:
            case DisconnectReason.TxFlooding:
            case DisconnectReason.NoCapabilityMatched:
                return EthDisconnectReason.UselessPeer;

            case DisconnectReason.DropWorstPeer:
                return EthDisconnectReason.TooManyPeers;

            case DisconnectReason.PeerRemoved:
            case DisconnectReason.PeerRefreshFailed:
                return EthDisconnectReason.DisconnectRequested;

            case DisconnectReason.ForwardSyncFailed:
                return EthDisconnectReason.DisconnectRequested;
            case DisconnectReason.GossipingInPoS:
                return EthDisconnectReason.BreachOfProtocol;
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

            case DisconnectReason.EthDisconnectRequested:
                return EthDisconnectReason.DisconnectRequested;
            case DisconnectReason.TcpSubSystemError:
                return EthDisconnectReason.TcpSubSystemError;
            case DisconnectReason.BreachOfProtocol:
                return EthDisconnectReason.BreachOfProtocol;
            case DisconnectReason.UselessPeer:
                return EthDisconnectReason.UselessPeer;
            case DisconnectReason.AlreadyConnected:
                return EthDisconnectReason.AlreadyConnected;
            case DisconnectReason.NullNodeIdentityReceived:
                return EthDisconnectReason.NullNodeIdentityReceived;
            case DisconnectReason.ClientQuitting:
                return EthDisconnectReason.ClientQuitting;
            case DisconnectReason.UnexpectedIdentity:
                return EthDisconnectReason.UnexpectedIdentity;
            case DisconnectReason.IdentitySameAsSelf:
                return EthDisconnectReason.IdentitySameAsSelf;
            case DisconnectReason.ReceiveMessageTimeout:
                return EthDisconnectReason.ReceiveMessageTimeout;
        }

        return EthDisconnectReason.Other;
    }
}
