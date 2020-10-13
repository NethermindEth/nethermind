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
using System.IO;
using Nethermind.Logging;
using Nethermind.Vault.Config;
using Nethermind.Vault.KeyStore;

namespace Nethermind.Vault.Test
{
    public class VaultSealingForTestsHelper
    {
        private const string Key = "fragile potato army dinner inch enrich decline under scrap soup audit worth trend point cheese sand online parrot faith catch olympic dignity mail crouch";
        private const string VaultConfigFileName = "TestingVaultFile";
        private readonly IVaultConfig _vaultConfig;
        private readonly VaultSealingHelper _vaultSealingHelper;
        public VaultSealingForTestsHelper(IVaultConfig vaultConfig)
        {
            _vaultConfig = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
            var passwordProvider = new VaultPasswordProvider(_vaultConfig, new Nethermind.KeyStore.PasswordProviderHelper());
            var vaultKeyStoreFacade = new VaultKeyStoreFacade(passwordProvider);
            _vaultSealingHelper = new VaultSealingHelper(vaultKeyStoreFacade, _vaultConfig);
        }


        public void Unseal()
        {
            SetUp();
            _vaultSealingHelper.Unseal();
        }

        public void Seal()
        {
            var vaultSealingHelper = InitHelper();
            _vaultSealingHelper.Seal();
            TearDown();
        }

        private VaultSealingHelper InitHelper()
        {
            var passwordProvider = new VaultPasswordProvider(_vaultConfig, new Nethermind.KeyStore.PasswordProviderHelper());
            var vaultKeyStoreFacade = new VaultKeyStoreFacade(passwordProvider);
            return new VaultSealingHelper(vaultKeyStoreFacade, _vaultConfig);
        }

        private void SetUp()
        {
            var vaultFilePath = _vaultConfig.VaultKeyFile.GetApplicationResourcePath();
            if (!File.Exists(vaultFilePath))
            {
                File.Create(vaultFilePath).Close();
                File.WriteAllText(vaultFilePath, Key);
            }
        }

        private void TearDown()
        {
            var vaultFilePath = _vaultConfig.VaultKeyFile.GetApplicationResourcePath();
            if (File.Exists(vaultFilePath))
            {
                File.Delete(vaultFilePath);
            }
        }
    }
}
