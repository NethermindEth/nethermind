// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Nethermind.Crypto
{
    // based on https://github.com/integrativesoft/CrossProtectedData
    public abstract partial class ProtectedData
    {
        private interface IProtector
        {
            byte[] Protect(byte[] userData, byte[] optionalEntropy, DataProtectionScope scope);
            byte[] Unprotect(byte[] encryptedData, byte[] optionalEntropy, DataProtectionScope scope);
        }

        private static readonly IProtector _protector = CreateProtector();

        private static IProtector CreateProtector()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new DpapiWrapper() : (IProtector)new AspNetWrapper();
        }

        protected static byte[] Protect(byte[] userData, byte[] optionalEntropy, DataProtectionScope scope) => _protector.Protect(userData, optionalEntropy, scope);

        protected static byte[] Unprotect(byte[] encryptedData, byte[] optionalEntropy, DataProtectionScope scope) => _protector.Unprotect(encryptedData, optionalEntropy, scope);
    }
}
