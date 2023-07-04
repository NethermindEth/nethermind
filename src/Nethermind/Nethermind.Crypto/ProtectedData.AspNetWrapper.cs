// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Nethermind.Crypto
{
    public partial class ProtectedData
    {
        private class AspNetWrapper : IProtector
        {
            private const string AppName = "Nethermind";
            private const string BaseName = AppName + "_";
            private const string ProtectionDir = "protection_keys";

            private readonly string _keyStoreDir;

            public AspNetWrapper(string keyStoreDir)
            {
                _keyStoreDir = keyStoreDir;
            }

            public byte[] Protect(byte[] userData, byte[] optionalEntropy, DataProtectionScope scope)
            {
                var protector = GetProtector(scope, optionalEntropy);
                return protector.Protect(userData);
            }

            public byte[] Unprotect(byte[] encryptedData, byte[] optionalEntropy, DataProtectionScope scope)
            {
                var protector = GetProtector(scope, optionalEntropy);
                return protector.Unprotect(encryptedData);
            }

            private IDataProtector GetProtector(DataProtectionScope scope, byte[] optionalEntropy)
            {
                return scope == DataProtectionScope.CurrentUser
                    ? GetUserProtector(optionalEntropy)
                    : GetMachineProtector(optionalEntropy);
            }

            private IDataProtector GetUserProtector(byte[] optionalEntropy)
            {
                string path = Path.Combine(_keyStoreDir, ProtectionDir);
                DirectoryInfo info = new(path);
                IDataProtectionProvider provider = DataProtectionProvider.Create(info);
                string purpose = CreatePurpose(optionalEntropy);

                return provider.CreateProtector(purpose);
            }

            private IDataProtector GetMachineProtector(byte[] optionalEntropy)
            {
                var provider = DataProtectionProvider.Create(AppName);
                var purpose = CreatePurpose(optionalEntropy);
                return provider.CreateProtector(purpose);
            }

            private string CreatePurpose(byte[] optionalEntropy)
            {
                var result = BaseName + Convert.ToBase64String(optionalEntropy);
                return Uri.EscapeDataString(result);
            }
        }
    }
}
