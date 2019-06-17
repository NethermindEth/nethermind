/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.DataMarketplace.Consumers.Domain;
using Nethermind.DataMarketplace.Consumers.Repositories;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories
{
    public class ProviderMongoRepository : IProviderRepository
    {
        private readonly IMongoDatabase _database;

        public ProviderMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public async Task<IReadOnlyList<DataHeaderInfo>> GetDataHeadersAsync()
            => await Deposits.AsQueryable()
                .Select(d => new DataHeaderInfo(d.DataHeader.Id, d.DataHeader.Name, d.DataHeader.Description))
                .Distinct()
                .ToListAsync();

        public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync()
            => await Deposits.AsQueryable()
                .Select(d => new ProviderInfo(d.DataHeader.Provider.Name, d.DataHeader.Provider.Address))
                .Distinct()
                .ToListAsync();

        private IMongoCollection<DepositDetails> Deposits => _database.GetCollection<DepositDetails>("deposits");
    }
}