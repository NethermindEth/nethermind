// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthMessageBase : MessageBase
    {
        public Signature Signature { get; set; } = null!;
        public PublicKey PublicKey { get; set; } = null!;
        public byte[] Nonce { get; set; } = null!;
        public int Version { get; set; } = 4;
    }
}
