// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.Vault.Config;

namespace Nethermind.Vault.KeyStore
{
    public class VaultKeyStoreFacade : IVaultKeyStoreFacade
    {
        private readonly IPasswordProvider _passwordProvider;
        public VaultKeyStoreFacade(IPasswordProvider passwordProvider)
        {
            _passwordProvider = passwordProvider ?? throw new ArgumentNullException(nameof(passwordProvider));
        }

        public string GetKey()
        {
            var password = _passwordProvider.GetPassword(null);
            return password.Unsecure();
        }
    }
}
