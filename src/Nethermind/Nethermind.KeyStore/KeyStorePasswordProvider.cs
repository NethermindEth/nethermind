// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            if (password is null && AlternativeProvider is not null)
            {
                password = AlternativeProvider.GetPassword(address);
            }

            return password;
        }
    }
}
