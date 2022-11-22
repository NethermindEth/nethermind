// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class EncryptionHandshake
    {
        public EncryptionSecrets Secrets { get; set; }
        public byte[] InitiatorNonce { get; set; }
        public byte[] RecipientNonce { get; set; }
        public PublicKey RemoteNodeId { get; set; }
        public PublicKey RemoteEphemeralPublicKey { get; set; }
        public PrivateKey EphemeralPrivateKey { get; set; }

        public Packet AuthPacket { get; set; }
        public Packet AckPacket { get; set; }
    }
}
