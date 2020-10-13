//  Copyright (c) 2020 Demerzel Solutions Limited
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

using System;
using System.Security;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Vault.Config;

namespace Nethermind.Vault.KeyStore
{
    public class VaultPasswordProvider : IPasswordProvider
    {
        private readonly IVaultConfig _vaultConfig;
        private readonly PasswordProviderHelper _passwordProviderHelper;
        
        public VaultPasswordProvider(IVaultConfig vaultConfig, PasswordProviderHelper passwordProviderHelper)
        {
            _vaultConfig = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
            _passwordProviderHelper = passwordProviderHelper ?? throw new ArgumentNullException(nameof(passwordProviderHelper));
        }

        public SecureString GetPassword(int? passwordIndex = null)
        {
            var filePath = _vaultConfig.VaultKeyFile.GetApplicationResourcePath();
            var passwordFromFile = _passwordProviderHelper.GetPasswordFromFile(filePath);
            return passwordFromFile != null ?
                   passwordFromFile 
                   : _passwordProviderHelper.GetPasswordFromConsole($"Provide passsphrase to unlock Vault");
        }
    }
}
