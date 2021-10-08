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

using System;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Repositories;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class ConfigManager : IConfigManager
    {
        private readonly NdmConfig _config;
        private readonly IConfigRepository _repository;

        public ConfigManager(NdmConfig config, IConfigRepository repository)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config), "NDM config was not provided.");
            _repository = repository ?? throw new ArgumentNullException(nameof(repository), "NDM config repository was not provided.");
        }

        public async Task<NdmConfig?> GetAsync(string id)
        {
            if (!_config.StoreConfigInDatabase)
            {
                return _config;
            }

            return await _repository.GetAsync(id);
        }

        public async Task UpdateAsync(NdmConfig config)
        {
            if (!_config.StoreConfigInDatabase)
            {
                return;
            }

            var existingConfig = await _repository.GetAsync(config.Id);
            if (existingConfig is null)
            {
                await _repository.AddAsync(config);
                return;
            }

            await _repository.UpdateAsync(config);
        }
    }
}