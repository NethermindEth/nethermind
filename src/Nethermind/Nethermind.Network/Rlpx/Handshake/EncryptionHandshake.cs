// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class EncryptionHandshake
    {
        public EncryptionSecrets Secrets { get; set; } = null!;
        public byte[] InitiatorNonce { get; set; } = null!;
        public byte[] RecipientNonce { get; set; } = null!;
        public PublicKey RemoteNodeId { get; set; } = null!;
        public PublicKey RemoteEphemeralPublicKey { get; set; } = null!;
        public PrivateKey EphemeralPrivateKey { get; set; } = null!;

        public Packet AuthPacket { get; set; } = null!;
        public Packet AckPacket { get; set; } = null!;
    }
}
