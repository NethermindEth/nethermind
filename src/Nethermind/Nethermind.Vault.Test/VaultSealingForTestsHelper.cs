// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.KeyStore;
using NUnit.Framework;

namespace Nethermind.Vault.Test
{
    public class VaultSealingForTestsHelper
    {
        private const string Key = "forest step weird object extend boat ball unit canoe pull render monkey drink monitor behind supply brush frown alone rural minute level host clock";
        private const string VaultConfigFileName = "TestingVaultFile";
        private readonly IVaultConfig _vaultConfig;
        private readonly VaultSealingHelper _vaultSealingHelper;
        public VaultSealingForTestsHelper(IVaultConfig vaultConfig)
        {
            _vaultConfig = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
            _vaultConfig.VaultKeyFile = VaultConfigFileName;
            var passwordProvider = new FilePasswordProvider(a => Path.Combine(TestContext.CurrentContext.WorkDirectory, _vaultConfig.VaultKeyFile));
            var vaultKeyStoreFacade = new VaultKeyStoreFacade(passwordProvider);
            _vaultSealingHelper = new VaultSealingHelper(vaultKeyStoreFacade, _vaultConfig, LimboLogs.Instance.GetClassLogger<VaultSealingHelper>());
        }


        public async Task Unseal()
        {
            SetUp();
            await _vaultSealingHelper.Unseal();
        }

        public async Task Seal()
        {
            await _vaultSealingHelper.Seal();
            TearDown();
        }

        private string TestDir => TestContext.CurrentContext.WorkDirectory;

        private void SetUp()
        {
            var vaultFilePath = Path.Combine(TestDir, _vaultConfig.VaultKeyFile);
            if (!File.Exists(vaultFilePath))
            {
                File.Create(vaultFilePath).Close();
                File.WriteAllText(vaultFilePath, Key);
            }
        }

        private void TearDown()
        {
            string vaultFilePath = Path.Combine(TestDir, _vaultConfig.VaultKeyFile);
            if (File.Exists(vaultFilePath))
            {
                File.Delete(vaultFilePath);
            }
        }
    }
}
