// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using MongoDB.Driver;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Repositories;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories
{
    public class ConfigMongoRepository : IConfigRepository
    {
        private readonly IMongoDatabase _database;

        public ConfigMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<NdmConfig?> GetAsync(string id) => Configs.FindSync(c => c.Id == id).FirstOrDefaultAsync<NdmConfig?>();

        public Task AddAsync(NdmConfig config) => Configs.InsertOneAsync(config);

        public Task UpdateAsync(NdmConfig config) => Configs.ReplaceOneAsync(c => c.Id == config.Id, config);

        private IMongoCollection<NdmConfig> Configs => _database.GetCollection<NdmConfig>("configs");
    }
}
