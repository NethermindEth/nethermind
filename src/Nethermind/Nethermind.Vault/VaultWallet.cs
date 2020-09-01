//  Copyright (c) 2020 Demerzel Solutions Limited
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Vault.Styles;
using Newtonsoft.Json;

namespace Nethermind.Vault
{
    public class VaultWallet : IVaultWallet
    {
        private readonly IVaultService _vaultService;
        private readonly ILogger _logger;
        private ConcurrentDictionary<Address, string> accounts;

        public VaultWallet(IVaultService vaultService, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _vaultService = vaultService;
            accounts = new ConcurrentDictionary<Address, string>();
        }

        public async Task<Address[]> GetAccounts()
        {
            accounts = new ConcurrentDictionary<Address, string>();
            var args = new Dictionary<string, object>();

            var vault = await SetWalletVault();

            var result = await _vaultService.ListVaultKeys(_vaultConfig.Token, vault, args);
            dynamic keys = JsonConvert.DeserializeObject(result.Item2);
            foreach (var key in keys)
            {
                try
                {
                    string address = Convert.ToString(key.address);
                    // adds to accounts dict to have addresses assigned to key id's
                    string keyId = Convert.ToString(key.id);
                    accounts.TryAdd(new Address(address), keyId);
                    accounts.Keys.ToArray();
                }
                catch (ArgumentNullException)
                {
                }
            }

            return accounts.Keys.ToArray();
        }

        public async Task DeleteAccount(Address address)
        {
            var vault = await SetWalletVault();
            string keyId = await GetKeyIdByAddress(address);
            var result = await _initVault.DeleteVaultKey(_vaultConfig.Token, vault, keyId);
        }

        public async Task<Signature> Sign(Address address, Keccak message)
        {
            var vault = await SetWalletVault();
            string keyId = await GetKeyIdByAddress(address);
            var result = await _initVault.SignMessage(_vaultConfig.Token, vault, keyId, message.ToString());
            dynamic sig = JsonConvert.DeserializeObject(result.Item2);
            string signature = Convert.ToString(sig.signature);

            return new Signature(signature);
        }

        public async Task<bool> Verify(Address address, Keccak message, Signature signature)
        {
            var vault = await SetWalletVault();
            string keyId = await GetKeyIdByAddress(address);
            string sig = Convert.ToString(signature).Remove(0, 2);
            var result = await _initVault.VerifySignature(_vaultConfig.Token, vault, keyId, message.ToString(), sig);
            dynamic isVerified = JsonConvert.DeserializeObject(result.Item2);
            return isVerified.verified;
        }

        public async Task<Address> NewAccount(Dictionary<string, object> parameters)
        {
            KeyArgs keyArgs = parameters["keyArgs"] as KeyArgs;

            if (keyArgs == null)
            {
                keyArgs = new KeyArgs();
                keyArgs.Name = "name";
                keyArgs.Description = "description";
                keyArgs.Type = "asymmetric";
                keyArgs.Spec = "secp256k1";
                keyArgs.Usage = "sign/verify";
            }

            if (!parameters.ContainsKey("keyArgs")) throw new ArgumentNullException(nameof(parameters));
            
            var result = await _vaultService.CreateKey(vault, keyArgs.ToDictionary());
            dynamic key = JsonConvert.DeserializeObject(result.Item2);
            string address = Convert.ToString(key.address);
            var account = new Address(address);
            accounts.TryAdd(account, Convert.ToString(key.id));
            return account;
        }

        public async Task<string> GetKeyIdByAddress(Address address)
        {
            await GetAccounts();
            return accounts.FirstOrDefault(acc => acc.Key.Equals(address)).Value;
        }
    }
}