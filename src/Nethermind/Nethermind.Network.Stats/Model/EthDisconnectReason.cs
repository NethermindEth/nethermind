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
        Other = 0x10
    }

    public static class EthDisconnectReasonExtensions {
        public static DisconnectReason ToDisconnectReason(this EthDisconnectReason reason)
        {
            switch (reason)
            {
                case EthDisconnectReason.DisconnectRequested:
                    return DisconnectReason.EthDisconnectRequested;
                case EthDisconnectReason.TcpSubSystemError:
                    return DisconnectReason.TcpSubSystemError;
                case EthDisconnectReason.BreachOfProtocol:
                    return DisconnectReason.BreachOfProtocol;
                case EthDisconnectReason.UselessPeer:
                    return DisconnectReason.UselessPeer;
                case EthDisconnectReason.TooManyPeers:
                    return DisconnectReason.TooManyPeers;
                case EthDisconnectReason.AlreadyConnected:
                    return DisconnectReason.AlreadyConnected;
                case EthDisconnectReason.IncompatibleP2PVersion:
                    return DisconnectReason.IncompatibleP2PVersion;
                case EthDisconnectReason.NullNodeIdentityReceived:
                    return DisconnectReason.NullNodeIdentityReceived;
                case EthDisconnectReason.ClientQuitting:
                    return DisconnectReason.ClientQuitting;
                case EthDisconnectReason.UnexpectedIdentity:
                    return DisconnectReason.UnexpectedIdentity;
                case EthDisconnectReason.IdentitySameAsSelf:
                    return DisconnectReason.IdentitySameAsSelf;
                case EthDisconnectReason.ReceiveMessageTimeout:
                    return DisconnectReason.ReceiveMessageTimeout;
                default:
                    return DisconnectReason.Other;
            }
        }
    }
}
