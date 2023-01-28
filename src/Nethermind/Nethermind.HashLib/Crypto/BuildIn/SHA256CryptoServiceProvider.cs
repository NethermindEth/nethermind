// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.HashLib.Crypto.BuildIn
{
    internal class SHA256CryptoServiceProvider : HashCryptoBuildIn
    {
        public SHA256CryptoServiceProvider()
            : base(System.Security.Cryptography.SHA256.Create(), 64)
        {
        }
    }
}
