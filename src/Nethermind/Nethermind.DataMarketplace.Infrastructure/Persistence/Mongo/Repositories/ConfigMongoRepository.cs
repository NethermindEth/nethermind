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