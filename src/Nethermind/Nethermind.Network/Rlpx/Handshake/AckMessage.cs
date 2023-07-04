// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AckMessage : MessageBase
    {
        public PublicKey EphemeralPublicKey { get; set; }
        public byte[] Nonce { get; set; }
        public bool IsTokenUsed { get; set; }
    }
}
