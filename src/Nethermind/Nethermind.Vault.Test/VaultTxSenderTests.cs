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

using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Vault.Config;
using NUnit.Framework;

namespace Nethermind.Vault.Test
{
    public class VaultTxSenderTests
    {
        [Test]
        public void Can_Initialize_VaultTxSender_without_exceptions()
        {
            var vaultConfig = new VaultConfig();
            vaultConfig.VaultId = "deca2436-21ba-4ff5-b225-ad1b0b2f5c59";
            var _vaultService = new VaultService(vaultConfig, LimboLogs.Instance);

            IVaultWallet wallet = new VaultWallet(_vaultService, vaultConfig.VaultId, LimboLogs.Instance);
            ITxSigner vaultSigner = new VaultTxSigner(wallet, 1);

            var txSender = new VaultTxSender(vaultSigner, vaultConfig, 1);
        }
    }
}
