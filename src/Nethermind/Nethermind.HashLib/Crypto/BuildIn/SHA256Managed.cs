// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.HashLib.Crypto.BuildIn
{
    internal class SHA256Managed : HashCryptoBuildIn, IHasHMACBuildIn
    {
        public SHA256Managed()
            : base(System.Security.Cryptography.SHA256.Create(), 64)
        {
        }

        public virtual System.Security.Cryptography.HMAC GetBuildHMAC()
        {
            return new System.Security.Cryptography.HMACSHA256();
        }
    }
}
