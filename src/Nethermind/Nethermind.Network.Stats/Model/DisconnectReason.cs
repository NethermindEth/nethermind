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
    HardLimitTooManyPeers,
    SessionAlreadyExist,
    ReplacingSessionWithOppositeDirection,
    OppositeDirectionCleanup,
    BackgroundTaskFailure,
    Exception,

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
    EthSyncException,

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
        return disconnectReason switch
        {
            DisconnectReason.TooManyPeers => EthDisconnectReason.TooManyPeers,
            DisconnectReason.HardLimitTooManyPeers => EthDisconnectReason.TooManyPeers,
            DisconnectReason.SessionAlreadyExist or DisconnectReason.ReplacingSessionWithOppositeDirection or DisconnectReason.OppositeDirectionCleanup or DisconnectReason.DuplicatedConnection or DisconnectReason.SessionIdAlreadyExists => EthDisconnectReason.AlreadyConnected,
            DisconnectReason.ConnectionClosed or DisconnectReason.OutgoingConnectionFailed => EthDisconnectReason.TcpSubSystemError,
            DisconnectReason.IncompatibleP2PVersion => EthDisconnectReason.IncompatibleP2PVersion,
            DisconnectReason.InvalidGenesis or DisconnectReason.MissingForkId or DisconnectReason.InvalidForkId => EthDisconnectReason.BreachOfProtocol,
            DisconnectReason.ClientFiltered => EthDisconnectReason.DisconnectRequested,
            DisconnectReason.ProtocolInitTimeout => EthDisconnectReason.ReceiveMessageTimeout,
            DisconnectReason.InvalidNetworkId or DisconnectReason.SnapServerNotImplemented or DisconnectReason.TxFlooding or DisconnectReason.NoCapabilityMatched => EthDisconnectReason.UselessPeer,
            DisconnectReason.DropWorstPeer => EthDisconnectReason.TooManyPeers,
            DisconnectReason.PeerRemoved or DisconnectReason.PeerRefreshFailed => EthDisconnectReason.DisconnectRequested,
            DisconnectReason.ForwardSyncFailed => EthDisconnectReason.DisconnectRequested,
            DisconnectReason.GossipingInPoS => EthDisconnectReason.BreachOfProtocol,
            DisconnectReason.AppClosing => EthDisconnectReason.ClientQuitting,
            DisconnectReason.InvalidTxOrUncle or DisconnectReason.HeaderResponseTooLong or DisconnectReason.InconsistentHeaderBatch or DisconnectReason.UnexpectedHeaderHash or DisconnectReason.HeaderBatchOnDifferentBranch or DisconnectReason.UnexpectedParentHeader or DisconnectReason.InvalidHeader or DisconnectReason.InvalidReceiptRoot or DisconnectReason.EthSyncException => EthDisconnectReason.BreachOfProtocol,
            DisconnectReason.EthDisconnectRequested => EthDisconnectReason.DisconnectRequested,
            DisconnectReason.TcpSubSystemError => EthDisconnectReason.TcpSubSystemError,
            DisconnectReason.BreachOfProtocol => EthDisconnectReason.BreachOfProtocol,
            DisconnectReason.UselessPeer => EthDisconnectReason.UselessPeer,
            DisconnectReason.AlreadyConnected => EthDisconnectReason.AlreadyConnected,
            DisconnectReason.NullNodeIdentityReceived => EthDisconnectReason.NullNodeIdentityReceived,
            DisconnectReason.ClientQuitting => EthDisconnectReason.ClientQuitting,
            DisconnectReason.UnexpectedIdentity => EthDisconnectReason.UnexpectedIdentity,
            DisconnectReason.IdentitySameAsSelf => EthDisconnectReason.IdentitySameAsSelf,
            DisconnectReason.ReceiveMessageTimeout => EthDisconnectReason.ReceiveMessageTimeout,
            _ => EthDisconnectReason.Other,
        };
    }
}
