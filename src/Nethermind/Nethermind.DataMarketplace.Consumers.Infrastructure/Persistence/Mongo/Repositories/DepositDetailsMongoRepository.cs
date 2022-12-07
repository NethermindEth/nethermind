// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
