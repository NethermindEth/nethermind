// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Nethermind.Crypto
{
    public abstract partial class ProtectedData
    {
        [SupportedOSPlatform("windows")]
        private sealed class DpapiWrapper : IProtector
        {
            public byte[] Protect(byte[] userData, byte[] optionalEntropy, DataProtectionScope scope)
            {
                return System.Security.Cryptography.ProtectedData.Protect(userData, optionalEntropy, scope);
            }

            public byte[] Unprotect(byte[] encryptedData, byte[] optionalEntropy, DataProtectionScope scope)
            {
                return System.Security.Cryptography.ProtectedData.Unprotect(encryptedData, optionalEntropy, scope);
            }
        }
    }
}
