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
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.Wallet
{
    public class AccountUnlocker
    {
        private readonly IKeyStoreConfig _config;
        private readonly IWallet _wallet;
        private readonly ILogger _logger;
        private readonly IPasswordProvider _passwordProvider;

        public AccountUnlocker(IKeyStoreConfig config, IWallet wallet, ILogManager logManager, IPasswordProvider passwordProvider)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _logger = logManager?.GetClassLogger<AccountUnlocker>() ?? throw new ArgumentNullException(nameof(logManager));
            _passwordProvider = passwordProvider ?? throw new ArgumentNullException(nameof(passwordProvider));
        }
        
        public void UnlockAccounts()
        { 
            for (int i = 0; i < _config.UnlockAccounts.Length; i++)
            {
                string unlockAccount = _config.UnlockAccounts[i];
                if (unlockAccount != _config.BlockAuthorAccount)
                {
                    try
                    {
                        Address address = new Address(unlockAccount);
                        if (_wallet.UnlockAccount(address, _passwordProvider.GetPassword(address) ?? string.Empty.Secure(), TimeSpan.FromDays(1000)))
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
