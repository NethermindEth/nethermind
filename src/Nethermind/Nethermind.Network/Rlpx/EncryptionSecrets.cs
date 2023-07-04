// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Rlpx
{
    public class EncryptionSecrets
    {
        public KeccakHash EgressMac { get; set; }
        public KeccakHash IngressMac { get; set; }
        public byte[] AesSecret { get; set; }
        public byte[] MacSecret { get; set; }
        //        public byte[] Token { get; set; }
    }
}
