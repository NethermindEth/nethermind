// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Rlpx
{
    public class EncryptionSecrets
    {
        public KeccakHash EgressMac { get; set; } = null!;
        public KeccakHash IngressMac { get; set; } = null!;
        public byte[] AesSecret { get; set; } = null!;
        public byte[] MacSecret { get; set; } = null!;
        //        public byte[] Token { get; set; }
    }
}
