// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.Logging;

namespace Nethermind.Wallet
{
    public class ProtectedKeyStoreWallet : IWallet
    {
        private static readonly TimeSpan DefaultExpirationTime = TimeSpan.FromMinutes(5);

        private readonly IKeyStore _keyStore;
        private readonly IProtectedPrivateKeyFactory _protectedPrivateKeyFactory;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;

        private readonly LruCache<String, ProtectedPrivateKey> _unlockedAccounts;
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;

        public ProtectedKeyStoreWallet(IKeyStore keyStore, IProtectedPrivateKeyFactory protectedPrivateKeyFactory, ITimestamper timestamper, ILogManager logManager)
        {
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            _protectedPrivateKeyFactory = protectedPrivateKeyFactory ?? throw new ArgumentNullException(nameof(protectedPrivateKeyFactory));
            _timestamper = timestamper ?? Timestamper.Default;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            // maxCapacity - 100, is just an estimate here
            _unlockedAccounts = new LruCache<string, ProtectedPrivateKey>(100, nameof(ProtectedKeyStoreWallet));
        }

        public void Import(byte[] keyData, SecureString passphrase)
        {
            _keyStore.StoreKey(new PrivateKey(keyData), passphrase);
        }

        public Address[] GetAccounts() => _keyStore.GetKeyAddresses().Addresses.ToArray();

        public Address NewAccount(SecureString passphrase)
        {
            (PrivateKey privateKey, _) = _keyStore.GenerateKey(passphrase);
            return privateKey.Address;
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan? timeSpan)
        {
            if (address == Address.Zero)
            {
                return false;
            }
            else if (IsUnlocked(address))
            {
                return true;
            }
            else
            {
                (PrivateKey key, Result result) = _keyStore.GetKey(address, passphrase);
                if (result.ResultType == ResultType.Success)
                {
                    if (_logger.IsInfo) _logger.Info($"Unlocking account: {address}");
                    _unlockedAccounts.Set(key.Address.ToString(), _protectedPrivateKeyFactory.Create(key));
                    AccountUnlocked?.Invoke(this, new AccountUnlockedEventArgs(address));
                    return true;
                }

                if (_logger.IsError) _logger.Error($"Failed to unlock the account: {address}");
                return false;
            }
        }

        public bool LockAccount(Address address)
        {
            AccountLocked?.Invoke(this, new AccountLockedEventArgs(address));
            _unlockedAccounts.Delete(address.ToString());
            return true;
        }

        public bool IsUnlocked(Address address) => _unlockedAccounts.Contains(address.ToString());

        public Signature Sign(Keccak message, Address address, SecureString passphrase)
            => SignCore(message, address, () =>
            {
                if (passphrase is null) throw new SecurityException("Passphrase missing when trying to sign a message");
                return _keyStore.GetKey(address, passphrase).PrivateKey;
            });

        public Signature Sign(Keccak message, Address address) => SignCore(message, address, () => throw new SecurityException("Can only sign without passphrase when account is unlocked."));

        private Signature SignCore(Keccak message, Address address, Func<PrivateKey> getPrivateKeyWhenNotFound)
        {
            var protectedPrivateKey = (ProtectedPrivateKey)_unlockedAccounts.Get(address.ToString());
            using PrivateKey key = protectedPrivateKey is not null ? protectedPrivateKey.Unprotect() : getPrivateKeyWhenNotFound();
            var rs = SpanSecP256k1.SignCompact(message.Bytes, key.KeyBytes, out int v);
            return new Signature(rs, v);
        }
    }
}
