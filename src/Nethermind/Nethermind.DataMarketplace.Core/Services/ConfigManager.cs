// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
