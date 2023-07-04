// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public interface IHandshakeService
    {
        Packet Auth(PublicKey remoteNodeId, EncryptionHandshake handshake, bool preEip8Format = false);
        Packet Ack(EncryptionHandshake handshake, Packet auth);
        void Agree(EncryptionHandshake handshake, Packet ack);
    }
}
