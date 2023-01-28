// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Vault.Config;
using Newtonsoft.Json;
using NUnit.Framework;
using static Nethermind.Vault.VaultTxSender;

namespace Nethermind.Vault.Test
{
    public class VaultTxSenderTests
    {
        [Test]
        public void Can_Initialize_VaultTxSender_without_exceptions()
        {
            var vaultConfig = new VaultConfig();
            vaultConfig.VaultId = "1b16996e-3595-4985-816c-043345d22f8c";
            var _vaultService = new VaultService(vaultConfig, LimboLogs.Instance);

            IVaultWallet wallet = new VaultWallet(_vaultService, vaultConfig.VaultId, LimboLogs.Instance);
            ITxSigner vaultSigner = new VaultTxSigner(wallet, 1);
            Assert.DoesNotThrow(() => { new VaultTxSender(vaultSigner, vaultConfig, 1); });
        }
    }
}
