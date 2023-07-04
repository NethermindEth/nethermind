// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
