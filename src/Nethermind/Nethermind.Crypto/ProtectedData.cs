// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Nethermind.Crypto
{
    // based on https://github.com/integrativesoft/CrossProtectedData
    public abstract partial class ProtectedData
    {
        private readonly IProtector _protector;

        protected ProtectedData(string keyStoreDir)
        {
            _protector = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new DpapiWrapper() : new AspNetWrapper(keyStoreDir);
        }

        private interface IProtector
        {
            byte[] Protect(byte[] userData, byte[] optionalEntropy, DataProtectionScope scope);
            byte[] Unprotect(byte[] encryptedData, byte[] optionalEntropy, DataProtectionScope scope);
        }

        protected byte[] Protect(byte[] userData, byte[] optionalEntropy, DataProtectionScope scope) =>
            _protector.Protect(userData, optionalEntropy, scope);

        protected byte[] Unprotect(byte[] encryptedData, byte[] optionalEntropy, DataProtectionScope scope) =>
            _protector.Unprotect(encryptedData, optionalEntropy, scope);
    }
}
