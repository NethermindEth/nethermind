// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class AuthMessage : AuthMessageBase
    {
        public Keccak EphemeralPublicHash { get; set; }
        public bool IsTokenUsed { get; set; }
    }
}
