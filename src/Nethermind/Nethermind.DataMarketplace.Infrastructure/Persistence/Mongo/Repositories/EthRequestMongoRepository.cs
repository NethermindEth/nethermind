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
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo.Repositories
{
    public class EthRequestMongoRepository : IEthRequestRepository
    {
        private readonly IMongoDatabase _database;

        public EthRequestMongoRepository(IMongoDatabase database)
        {
            _database = database;
        }

        public Task<EthRequest?> GetLatestAsync(string host)
            => EthRequests.AsQueryable().Where(r => r.Host == host).FirstOrDefaultAsync()!;

        public Task AddAsync(EthRequest request) => EthRequests.InsertOneAsync(request);
        public Task UpdateAsync(EthRequest request) => EthRequests.ReplaceOneAsync(r => r.Id == request.Id, request);
        
        // MongoDB driver for C# doesn't support Date.Date comparison.
        public async Task<UInt256> SumDailyRequestsTotalValueAsync(DateTime date)
        {
            var previousDate = date.AddDays(-1);
            var nextDate = date.AddDays(1);
            var requests = await EthRequests.AsQueryable()
                .Where(r => r.RequestedAt > previousDate && r.RequestedAt < nextDate).ToListAsync();
            if (!requests.Any())
            {
                return 0;
            }
            
            var totalValue = UInt256.Zero;
            foreach (var request in requests)
            {
                totalValue += request.Value;
            }

            return totalValue;
        }

        private IMongoCollection<EthRequest> EthRequests => _database.GetCollection<EthRequest>("ethRequests");
    }
}