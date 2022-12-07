// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Databases;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.InMemory.Repositories
{
    public class DepositDetailsInMemoryRepository : IDepositDetailsRepository
    {
        private readonly DepositsInMemoryDb _db;
        private readonly IDepositUnitsCalculator _depositUnitsCalculator;

        public DepositDetailsInMemoryRepository(DepositsInMemoryDb db, IDepositUnitsCalculator depositUnitsCalculator)
        {
            _db = db;
            _depositUnitsCalculator = depositUnitsCalculator;
        }

        public Task<DepositDetails?> GetAsync(Keccak id) => Task.FromResult(_db.Get(id));

        public async Task<PagedResult<DepositDetails>> BrowseAsync(GetDeposits query)
        {
            if (query is null)
            {
                return PagedResult<DepositDetails>.Empty;
            }

            var deposits = _db.GetAll();
            if (!deposits.Any())
            {
                return PagedResult<DepositDetails>.Empty;
            }

            var filteredDeposits = deposits.AsEnumerable();
            if (query.OnlyPending)
            {
                filteredDeposits = filteredDeposits.Where(d => !d.Rejected && !d.RefundClaimed &&
                                                               d.Transaction?.State == TransactionState.Pending ||
                                                               d.ClaimedRefundTransaction?.State ==
                                                               TransactionState.Pending);
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
                foreach (var deposit in filteredDeposits)
                {
                    uint consumedUnits = await _depositUnitsCalculator.GetConsumedAsync(deposit);
                    deposit.SetConsumedUnits(consumedUnits);
                }

                filteredDeposits = filteredDeposits.Where(d => !d.RefundClaimed &&
                                                               (d.ConsumedUnits < d.Deposit.Units) &&
                                                               (!(d.EarlyRefundTicket is null) ||
                                                               query.CurrentBlockTimestamp >= d.Deposit.ExpiryTime));
            }

            return filteredDeposits.OrderByDescending(d => d.Timestamp).ToArray().Paginate(query);
        }

        public Task AddAsync(DepositDetails deposit)
        {
            _db.Add(deposit);

            return Task.CompletedTask;
        }

        public Task UpdateAsync(DepositDetails deposit) => Task.CompletedTask;
    }
}
