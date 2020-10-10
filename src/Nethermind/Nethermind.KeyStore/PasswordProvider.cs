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
// 

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.KeyStore
{
    public class PasswordProvider : IPasswordProvider
    {
        private readonly IKeyStoreConfig _keyStoreConfig;
        public PasswordProvider(IKeyStoreConfig keyStoreConfig)
        {
            _keyStoreConfig = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
        }
        public SecureString GetBlockAuthorPassword()
        {
            SecureString passwordFromFile = null;
            var index = Array.IndexOf(_keyStoreConfig.UnlockAccounts, _keyStoreConfig.BlockAuthorAccount);
            if (index >= 0)
            {
                passwordFromFile = GetPassword(index);
            }

            return passwordFromFile != null ? passwordFromFile : GetPasswordFromConsole();
        }

        public SecureString GetPassword(int keyStoreConfigPasswordIndex)
        {
            string GetPasswordN(int n, string[] passwordsCollection) => passwordsCollection?.Length > 0 ? passwordsCollection[Math.Min(n, passwordsCollection.Length - 1)] : null;

            SecureString password = null;
            var passwordFile = GetPasswordN(keyStoreConfigPasswordIndex, _keyStoreConfig.PasswordFiles);
            if (passwordFile != null)
            {
                string blockAuthorPasswordFilePath = passwordFile.GetApplicationResourcePath();
                password = File.Exists(blockAuthorPasswordFilePath)
                    ? ReadFromFileToSecureString(blockAuthorPasswordFilePath)
                    : null;
            }

            password ??= GetPasswordN(keyStoreConfigPasswordIndex, _keyStoreConfig.Passwords)?.Secure();
            return password;
        }

        public SecureString GetPasswordFromConsole()
        {
            return ConsoleUtils.ReadSecret($"Provide password for validator account {_keyStoreConfig.BlockAuthorAccount}");
        }

        private SecureString ReadFromFileToSecureString(string filePath)
        {
            var whitespaces = new List<char>();
            var secureString = new SecureString();
            using (StreamReader stream = new StreamReader(filePath))
            {
                bool trimBeginFinished = false;
                while (stream.Peek() >= 0)
                {
                    var character = (char)stream.Read();
                    if (char.IsWhiteSpace(character))
                    {
                        if (trimBeginFinished)
                        {
                            whitespaces.Add(character);
                        }
                    }
                    else
                    {
                        trimBeginFinished = true;
                        if (whitespaces.Count != 0)
                        {
                            FillWhitespaceList(secureString, whitespaces);
                            whitespaces.Clear();
                        }

                        secureString.AppendChar(character);
                    }
                }
            }

            return secureString;
        }

        private void FillWhitespaceList(SecureString secureString, List<char> whitespaces)
        {
            foreach (var whitespace in whitespaces)
            {
                secureString.AppendChar(whitespace);
            }
        }
    }
}
