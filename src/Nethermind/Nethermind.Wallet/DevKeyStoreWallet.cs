// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.Logging;

namespace Nethermind.Wallet
{
    [DoNotUseInSecuredContext("For dev purposes only")]
    public class DevKeyStoreWallet : IWallet
    {
        private readonly IKeyStore _keyStore;
        private readonly ILogger _logger;

        private readonly Dictionary<Address, PrivateKey> _unlockedAccounts = [];
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;

        public DevKeyStoreWallet(IKeyStore keyStore, ILogManager logManager, bool createTestAccounts = true)
        {
            _keyStore = keyStore;
            _logger = logManager?.GetClassLogger<DevKeyStoreWallet>() ?? throw new ArgumentNullException(nameof(logManager));

            if (createTestAccounts)
            {
                this.SetupTestAccounts(3);
            }
        }

        public void Import(byte[] keyData, SecureString passphrase) => _keyStore.StoreKey(new PrivateKey(keyData), passphrase);

        public Address[] GetAccounts() => _keyStore.GetKeyAddresses().Addresses.ToArray();

        public Address NewAccount(SecureString passphrase)
        {
            (PrivateKey privateKey, _) = _keyStore.GenerateKey(passphrase);
            return privateKey.Address;
        }

        public bool UnlockAccount(Address address, SecureString passphrase) => UnlockAccount(address, passphrase, TimeSpan.FromSeconds(300));

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan? timeSpan)
        {
            if (address == Address.Zero)
            {
                return false;
            }

            if (_unlockedAccounts.ContainsKey(address)) return true;

            (PrivateKey key, Result result) = _keyStore.GetKey(address, passphrase);
            if (result.ResultType == ResultType.Success)
            {
                if (_logger.IsInfo) _logger.Info($"Unlocking account: {address}");
                _unlockedAccounts.Add(key.Address, key);
                AccountUnlocked?.Invoke(this, new AccountUnlockedEventArgs(address));
                return true;
            }

            if (_logger.IsError) _logger.Error($"Failed to unlock the account: {address}");
            return false;
        }

        public bool LockAccount(Address address)
        {
            AccountLocked?.Invoke(this, new AccountLockedEventArgs(address));
            return _unlockedAccounts.Remove(address);
        }

        public bool IsUnlocked(Address address) => _unlockedAccounts.ContainsKey(address);

        public bool TrySign(in ValueHash256 message, Address address, [NotNullWhen(true)] out Signature signature)
        {
            if (!_unlockedAccounts.TryGetValue(address, out PrivateKey key))
            {
                signature = null;
                return false;
            }

            signature = WalletSigner.Sign(in message, key);
            return true;
        }

        public bool TrySign(in ValueHash256 message, Address address, SecureString passphrase, [NotNullWhen(true)] out Signature signature) =>
            WalletSigner.TrySignWithPassphrase(_keyStore, in message, address, passphrase, out signature);
    }
}
