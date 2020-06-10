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
using System.Collections.Generic;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Secp256k1;
using Nethermind.Vault.Config;
using Nethermind.Wallet;
using provide;

namespace Nethermind.Vault
{
    public class VaultWallet: IVault
    {
        private readonly IVault _vault;
        private readonly IVaultManager _vaultManager;
        private readonly IVaultConfig _vaultConfig;
        private readonly ILogger _logger;
        private readonly provide.Vault _initVault;

        public VaultWallet(IVault vault, IVaultManager vaultManager, IVaultConfig vaultConfig, ILogManager logManager)
        {
            _vault = vault ?? throw new ArgumentNullException(nameof(vault));
            _vaultManager = vaultManager ?? throw new ArgumentNullException(nameof(vaultManager));
            _vaultConfig = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        
            _initVault = provide.Vault.InitVault(_vaultConfig.Token);
        }

        public void GetAddresses()
        {
            _vault.GetAddresses();
        }

        public void Sign(string vaultId, string keyId, string msg)
        {
            _initVault.SignMessage(_vaultConfig.Token, vaultId, keyId, msg);
        }

        public bool Verify()
        {
            return _vault.Verify();
        }
    }
}