//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.KeyStore
{
    public class KeyStorePasswordProvider : IPasswordProvider
    {
        private readonly IKeyStoreConfig _keyStoreConfig;
        private readonly PasswordProviderHelper _passwordProviderHelper;

        public KeyStorePasswordProvider(IKeyStoreConfig keyStoreConfig)
        {
            _keyStoreConfig = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
            _passwordProviderHelper = new PasswordProviderHelper();
        }
        public SecureString GetPassword(int? passwordIndex)
        {
            if (passwordIndex == null)
            {
                throw new ArgumentNullException("KeyStorePasswordProvider does not allow null as a password index value");
            }

            var keyStoreConfigPasswordIndex = passwordIndex.Value;
            string GetPasswordN(int n, string[] passwordsCollection) => passwordsCollection?.Length > 0 ? passwordsCollection[Math.Min(n, passwordsCollection.Length - 1)] : null;

            SecureString password = null;
            var passwordFile = GetPasswordN(keyStoreConfigPasswordIndex, _keyStoreConfig.PasswordFiles);
            if (passwordFile != null)
            {
                string blockAuthorPasswordFilePath = passwordFile.GetApplicationResourcePath();
                password = File.Exists(blockAuthorPasswordFilePath)
                    ? _passwordProviderHelper.GetPasswordFromFile(blockAuthorPasswordFilePath)
                    : null;
            }

            password ??= GetPasswordN(keyStoreConfigPasswordIndex, _keyStoreConfig.Passwords)?.Secure();
            return password;
        }
    }
}
