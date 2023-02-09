// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.HashLib.Crypto.BuildIn
{
    internal class MD5CryptoServiceProvider : HashCryptoBuildIn, IHasHMACBuildIn
    {
        public MD5CryptoServiceProvider()
            : base(System.Security.Cryptography.MD5.Create(), 64)
        {
        }

        public virtual System.Security.Cryptography.HMAC GetBuildHMAC()
        {
            return new System.Security.Cryptography.HMACMD5();
        }
    }
}
