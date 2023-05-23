// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AckEip8Message : MessageBase
    {
        public PublicKey EphemeralPublicKey { get; set; }
        public byte[] Nonce { get; set; }
        public byte Version { get; set; } = 0x04;
    }
}
