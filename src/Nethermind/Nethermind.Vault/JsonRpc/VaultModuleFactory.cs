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
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Vault.Config;

namespace Nethermind.Vault.JsonRpc
{
    public class VaultModuleFactory : ModuleFactoryBase<IVaultModule>
    {
        private readonly ILogManager _logManager;
        private readonly IVaultConfig _vaultConfig;
        
        public VaultModuleFactory(
            IVaultConfig vaultConfig,
            ILogManager logManager)
        {
            _vaultConfig = vaultConfig ?? throw new ArgumentNullException(nameof(vaultConfig));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public override IVaultModule Create()
        {
            return new VaultModule(_vaultConfig, _logManager);
        }
    }
}