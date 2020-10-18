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
using System.Security;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.KeyStore
{
    public class KeyStorePasswordProvider : BasePasswordProvider, IKeyStorePasswordProvider
    {
        private readonly IKeyStoreConfig _keyStoreConfig;
        private readonly FilePasswordProvider _filePasswordProvider;

        public KeyStorePasswordProvider(IKeyStoreConfig keyStoreConfig)
        {
            _keyStoreConfig = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
            _filePasswordProvider = new FilePasswordProvider();
        }

        public string Account { private get; set; }

        public override SecureString GetPassword()
        {
            string GetPasswordN(int n, string[] passwordsCollection) => passwordsCollection?.Length > 0 ? passwordsCollection[Math.Min(n, passwordsCollection.Length - 1)] : null;
            SecureString password = null;
            var keyStoreConfigPasswordIndex = Array.IndexOf(_keyStoreConfig.UnlockAccounts, Account);
            if (keyStoreConfigPasswordIndex >= 0)
            {
                var passwordFile = GetPasswordN(keyStoreConfigPasswordIndex, _keyStoreConfig.PasswordFiles);
                if (passwordFile != null)
                {
                    string passwordFilePath = passwordFile.GetApplicationResourcePath();
                    _filePasswordProvider.FileName = passwordFilePath;
                    password = _filePasswordProvider.GetPassword();
                }

                password ??= GetPasswordN(keyStoreConfigPasswordIndex, _keyStoreConfig.Passwords)?.Secure();
            }

            if (password == null && AlternativeProvider != null)
            {
                password = AlternativeProvider.GetPassword();
            }

            return password;
        }
    }
}
