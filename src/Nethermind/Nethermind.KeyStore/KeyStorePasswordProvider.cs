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

using System;
using System.Security;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;

namespace Nethermind.KeyStore
{
    public class KeyStorePasswordProvider : BasePasswordProvider
    {
        private readonly IKeyStoreConfig _keyStoreConfig;
        private readonly FilePasswordProvider _filePasswordProvider;

        public KeyStorePasswordProvider(IKeyStoreConfig keyStoreConfig)
        {
            _keyStoreConfig = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
            _filePasswordProvider = new FilePasswordProvider(Map);
        }

        private static string GetNthOrLast(int n, string[] items)
            => items?.Length > 0 ? items[Math.Min(n, items.Length - 1)] : null;
        
        private string Map(Address address)
        {
            string result = string.Empty;

            int keyStoreConfigPasswordIndex = _keyStoreConfig.FindUnlockAccountIndex(address);
            if (keyStoreConfigPasswordIndex >= 0)
            {
                var passwordFile = GetNthOrLast(keyStoreConfigPasswordIndex, _keyStoreConfig.PasswordFiles);
                result = passwordFile ?? string.Empty;
            }

            return result;
        }
        
        public override SecureString GetPassword(Address address)
        {
            SecureString password = null;
            int keyStoreConfigPasswordIndex = _keyStoreConfig.FindUnlockAccountIndex(address);
            
            if (keyStoreConfigPasswordIndex >= 0)
            {
                password = _filePasswordProvider.GetPassword(address);
                password ??= GetNthOrLast(keyStoreConfigPasswordIndex, _keyStoreConfig.Passwords)?.Secure();
            }

            if (password == null && AlternativeProvider != null)
            {
                password = AlternativeProvider.GetPassword(address);
            }

            return password;
        }
    }
}
