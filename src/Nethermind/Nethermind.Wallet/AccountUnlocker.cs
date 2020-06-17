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
using System.IO;
using System.IO.Abstractions;
using System.Security;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.Wallet
{
    public class AccountUnlocker
    {
        private readonly IKeyStoreConfig _config;
        private readonly IWallet _wallet;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        public AccountUnlocker(IKeyStoreConfig config, IWallet wallet, IFileSystem fileSystem, ILogManager logManager)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = logManager?.GetClassLogger<AccountUnlocker>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public void UnlockAccounts()
        {
            string GetPasswordN(int n, string[] passwords) => passwords?.Length > 0 ? passwords[Math.Min(n, passwords.Length - 1)] : null;
            SecureString GetPassword(int n)
            {
                string password = GetPasswordN(n, _config.PasswordFiles);
                if (password != null)
                {
                    string passwordPath = password.GetApplicationResourcePath();
                    password = _fileSystem.File.Exists(passwordPath)
                        ? _fileSystem.File.ReadAllText(passwordPath).Trim()
                        : null;
                }
                
                password ??= GetPasswordN(n, _config.Passwords) ?? string.Empty;

                return password.Secure();
            }

            for (int i = 0; i < _config.UnlockAccounts.Length; i++)
            {
                string unlockAccount = _config.UnlockAccounts[i];
                if (unlockAccount != _config.BlockAuthorAccount)
                {
                    try
                    {
                        Address address = new Address(unlockAccount);
                        if (_wallet.UnlockAccount(address, GetPassword(i), TimeSpan.FromDays(1000)))
                        {
                            if (_logger.IsInfo) _logger.Info($"Unlocked account: {unlockAccount}");
                        }
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error($"Couldn't unlock account {unlockAccount}.", e);
                    }
                }
            }
        }
    }
}
