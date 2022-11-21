// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Network.Rlpx
{
    public class EncryptionSecrets
    {
        public KeccakDigest EgressMac { get; set; }
        public KeccakDigest IngressMac { get; set; }
        public byte[] AesSecret { get; set; }
        public byte[] MacSecret { get; set; }
        //        public byte[] Token { get; set; }
    }
}
