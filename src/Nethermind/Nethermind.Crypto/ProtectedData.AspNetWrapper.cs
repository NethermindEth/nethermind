//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
                if (scope == DataProtectionScope.CurrentUser)
                {
                    return GetUserProtector(optionalEntropy);
                }
                else
                {
                    return GetMachineProtector(optionalEntropy);
                }
            }

            private IDataProtector GetUserProtector(byte[] optionalEntropy)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var path = Path.Combine(appData, AppName);
                var info = new DirectoryInfo(path);
                var provider = DataProtectionProvider.Create(info);
                var purpose = CreatePurpose(optionalEntropy);
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
