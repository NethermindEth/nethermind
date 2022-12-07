// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
