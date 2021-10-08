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

using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Mongo.Repositories
{
    public class DepositDetailsMongoRepository : IDepositDetailsRepository
    {
        private readonly IMongoDatabase _database;
        private readonly IDepositUnitsCalculator _depositUnitsCalculator;

        public DepositDetailsMongoRepository(IMongoDatabase database, IDepositUnitsCalculator depositUnitsCalculator)
        {
            _database = database;
            _depositUnitsCalculator = depositUnitsCalculator;
        }

        public Task<DepositDetails?> GetAsync(Keccak id)
            => Deposits.Find(c => c.Id == id).FirstOrDefaultAsync()!;

        public async Task<PagedResult<DepositDetails>> BrowseAsync(GetDeposits query)
        {
            if (query is null)
            {
                return PagedResult<DepositDetails>.Empty;
            }

            var deposits = Deposits.AsQueryable();
            if (query.OnlyPending || query.OnlyUnconfirmed || query.OnlyNotRejected || query.EligibleToRefund)
            {
                //MongoDB unsupported predicate: (d.Confirmations < d.RequiredConfirmations) - maybe due to uint type?
                var allDeposits = await deposits.ToListAsync();
                var filteredDeposits = allDeposits.AsEnumerable();
                if (query.OnlyPending)
                {
                    filteredDeposits = filteredDeposits.Where(d => !d.Rejected && !d.RefundClaimed &&
                                                                   (d.Transaction?.State == TransactionState.Pending ||
                                                                   d.ClaimedRefundTransaction?.State ==
                                                                   TransactionState.Pending));
                }

                if (query.OnlyUnconfirmed)
                {
                    filteredDeposits = filteredDeposits.Where(d => d.ConfirmationTimestamp == 0 ||
                                                                   d.Confirmations < d.RequiredConfirmations);
                }

                if (query.OnlyNotRejected)
                {
                    filteredDeposits = filteredDeposits.Where(d => !d.Rejected);
                }

                if (query.EligibleToRefund)
                {
                    foreach (var deposit in deposits)
                    {
                        uint consumedUnits = await _depositUnitsCalculator.GetConsumedAsync(deposit);
                        deposit.SetConsumedUnits(consumedUnits);
                    }
                    
                    filteredDeposits = filteredDeposits.Where(d => !d.RefundClaimed && (d.ConsumedUnits < d.Deposit.Units) &&
                                                                   (!(d.EarlyRefundTicket is null) ||
                                                                    query.CurrentBlockTimestamp >= d.Deposit.ExpiryTime
                                                                   ));
                }

                return filteredDeposits.OrderByDescending(d => d.Timestamp).ToArray().Paginate(query);
            }

            return await deposits.OrderByDescending(d => d.Timestamp).PaginateAsync(query);
        }

        public Task AddAsync(DepositDetails deposit)
            => Deposits.InsertOneAsync(deposit);

        public Task UpdateAsync(DepositDetails deposit)
            => Deposits.ReplaceOneAsync(c => c.Id == deposit.Id, deposit);

        private IMongoCollection<DepositDetails> Deposits => _database.GetCollection<DepositDetails>("deposits");
    }
}
