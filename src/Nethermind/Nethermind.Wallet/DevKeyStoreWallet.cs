// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Secp256k1;

namespace Nethermind.Wallet
{
    [DoNotUseInSecuredContext("For dev purposes only")]
    public class DevKeyStoreWallet : IWallet
    {
        private readonly IKeyStore _keyStore;
        private readonly ILogger _logger;

        private readonly Dictionary<Address, PrivateKey> _unlockedAccounts = new Dictionary<Address, PrivateKey>();
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;

        public DevKeyStoreWallet(IKeyStore keyStore, ILogManager logManager, bool createTestAccounts = true)
        {
            _keyStore = keyStore;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            if (createTestAccounts)
            {
                this.SetupTestAccounts(3);
            }
        }

        public void Import(byte[] keyData, SecureString passphrase)
        {
            _keyStore.StoreKey(new PrivateKey(keyData), passphrase);
        }

        public Address[] GetAccounts()
        {
            return _keyStore.GetKeyAddresses().Addresses.ToArray();
        }

        public Address NewAccount(SecureString passphrase)
        {
            (PrivateKey privateKey, _) = _keyStore.GenerateKey(passphrase);
            return privateKey.Address;
        }

        public bool UnlockAccount(Address address, SecureString passphrase)
        {
            return UnlockAccount(address, passphrase, TimeSpan.FromSeconds(300));
        }

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

        public Signature Sign(Keccak message, Address address, SecureString passphrase)
        {
            PrivateKey key;
            if (_unlockedAccounts.ContainsKey(address))
            {
                key = _unlockedAccounts[address];
            }
            else
            {
                if (passphrase is null) throw new SecurityException("Passphrase missing when trying to sign a message");

                key = _keyStore.GetKey(address, passphrase).PrivateKey;
            }

            var rs = Proxy.SignCompact(message.ToByteArray(), key.KeyBytes, out int v);
            return new Signature(rs, v);
        }

        public Signature Sign(Keccak message, Address address)
        {
            PrivateKey key;
            if (_unlockedAccounts.ContainsKey(address))
            {
                key = _unlockedAccounts[address];
            }
            else
            {
                throw new SecurityException("Can only sign without passphrase when account is unlocked.");
            }

            var rs = Proxy.SignCompact(message.ToByteArray(), key.KeyBytes, out int v);
            return new Signature(rs, v);
        }
    }
}
