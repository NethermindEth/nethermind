// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.Model
{
    /// <summary>
    /// Eth network level disconnect reason
    /// </summary>
    public enum EthDisconnectReason : byte
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
        MultipleHeaderDependencies = 0x0c,
        Other = 0x10
    }

    public static class EthDisconnectReasonExtensions
    {
        public static DisconnectReason ToDisconnectReason(this EthDisconnectReason reason)
        {
            return reason switch
            {
                EthDisconnectReason.DisconnectRequested => DisconnectReason.EthDisconnectRequested,
                EthDisconnectReason.TcpSubSystemError => DisconnectReason.TcpSubSystemError,
                EthDisconnectReason.BreachOfProtocol => DisconnectReason.BreachOfProtocol,
                EthDisconnectReason.UselessPeer => DisconnectReason.UselessPeer,
                EthDisconnectReason.TooManyPeers => DisconnectReason.TooManyPeers,
                EthDisconnectReason.AlreadyConnected => DisconnectReason.AlreadyConnected,
                EthDisconnectReason.IncompatibleP2PVersion => DisconnectReason.IncompatibleP2PVersion,
                EthDisconnectReason.NullNodeIdentityReceived => DisconnectReason.NullNodeIdentityReceived,
                EthDisconnectReason.ClientQuitting => DisconnectReason.ClientQuitting,
                EthDisconnectReason.UnexpectedIdentity => DisconnectReason.UnexpectedIdentity,
                EthDisconnectReason.IdentitySameAsSelf => DisconnectReason.IdentitySameAsSelf,
                EthDisconnectReason.ReceiveMessageTimeout => DisconnectReason.ReceiveMessageTimeout,
                _ => DisconnectReason.Other,
            };
        }
    }
}
