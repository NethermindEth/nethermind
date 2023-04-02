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
using Nethermind.Logging;
using Nethermind.Secp256k1;

namespace Nethermind.Wallet
{
    [DoNotUseInSecuredContext("For dev purposes only")]
    public class DevWallet : IWallet
    {
        private const string AnyPassword = "#DEV_ACCOUNT_NETHERMIND_ANY_PASSWORD#";
        private static byte[] _keySeed = new byte[32];
        private readonly ILogger _logger;
        private Dictionary<Address, bool> _isUnlocked = new Dictionary<Address, bool>();
        private Dictionary<Address, PrivateKey> _keys = new Dictionary<Address, PrivateKey>();
        private Dictionary<Address, string> _passwords = new Dictionary<Address, string>();
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;

        public DevWallet(IWalletConfig walletConfig, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _keySeed[31] = 1;
            for (int i = 0; i < walletConfig?.DevAccounts; i++)
            {
                PrivateKey key = new PrivateKey(_keySeed);
                _keys.Add(key.Address, key);
                _passwords.Add(key.Address, AnyPassword);
                _isUnlocked.Add(key.Address, true);
                _keySeed[31]++;
            }
        }

        public void Import(byte[] keyData, SecureString passphrase)
        {
            throw new NotSupportedException();
        }

        public Address[] GetAccounts()
        {
            return _keys.Keys.ToArray();
        }

        public Address NewAccount(SecureString passphrase)
        {
            using var privateKeyGenerator = new PrivateKeyGenerator();
            PrivateKey key = privateKeyGenerator.Generate();
            _keys.Add(key.Address, key);
            _isUnlocked.Add(key.Address, true);
            _passwords.Add(key.Address, passphrase.Unsecure());
            return key.Address;
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan? timeSpan)
        {

            if (address is null || address == Address.Zero)
            {
                return false;
            }

            if (!_passwords.ContainsKey(address))
            {
                if (_logger.IsError) _logger.Error("Account does not exist.");
                return false;
            }

            if (!CheckPassword(address, passphrase))
            {
                if (_logger.IsError) _logger.Error("Password is invalid.");
                return false;
            }

            AccountUnlocked?.Invoke(this, new AccountUnlockedEventArgs(address));
            _isUnlocked[address] = true;

            return true;
        }

        public bool LockAccount(Address address)
        {
            if (!_passwords.ContainsKey(address))
            {
                if (_logger.IsError) _logger.Error("Account does not exist.");
                return false;
            }

            AccountLocked?.Invoke(this, new AccountLockedEventArgs(address));
            _isUnlocked[address] = false;

            return true;
        }
        public Signature Sign(Keccak message, Address address, SecureString passphrase)
        {
            if (!_isUnlocked.ContainsKey(address)) throw new SecurityException("Account does not exist.");

            if (!_isUnlocked[address] && !CheckPassword(address, passphrase)) throw new SecurityException("Cannot sign without password or unlocked account.");

            return Sign(message, address);
        }

        public bool IsUnlocked(Address address) => _isUnlocked.TryGetValue(address, out var unlocked) && unlocked;

        private bool CheckPassword(Address address, SecureString passphrase)
        {
            return _passwords[address] == AnyPassword || passphrase?.Unsecure() == _passwords[address];
        }

        public Signature Sign(Keccak message, Address address)
        {
            var rs = Proxy.SignCompact(message.ToByteArray(), _keys[address].KeyBytes, out int v);
            return new Signature(rs, v);
        }
    }
}
