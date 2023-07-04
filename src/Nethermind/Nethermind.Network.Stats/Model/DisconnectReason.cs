// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
        Other = 0x10
    }
}
