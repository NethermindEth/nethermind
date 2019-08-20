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

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Store;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories
{
    public class DepositDetailsRocksRepository : IDepositDetailsRepository
    {
        private readonly IDb _database;
        private readonly IRlpDecoder<DepositDetails> _rlpDecoder;

        public DepositDetailsRocksRepository(IDb database, IRlpDecoder<DepositDetails> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
        }

        public async Task<DepositDetails> GetAsync(Keccak id)
        {
            await Task.CompletedTask;

            return Decode(_database.Get(id));
        }

        public Task<PagedResult<DepositDetails>> BrowseAsync(GetDeposits query)
        {
            if (query is null)
            {
                return Task.FromResult(PagedResult<DepositDetails>.Empty);
            }

            var depositsBytes = _database.GetAll();
            if (depositsBytes.Length == 0)
            {
                return Task.FromResult(PagedResult<DepositDetails>.Empty);
            }

            var deposits = new DepositDetails[depositsBytes.Length];
            for (var i = 0; i < depositsBytes.Length; i++)
            {
                deposits[i] = Decode(depositsBytes[i]);
            }

            var filteredDeposits = deposits.AsEnumerable();
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
                filteredDeposits = filteredDeposits.Where(d => !d.RefundClaimed &&
                                                               (!(d.EarlyRefundTicket is null) ||
                                                                query.CurrentBlockTimestamp >= d.Deposit.ExpiryTime
                                                               ));
            }

            return Task.FromResult(filteredDeposits.OrderByDescending(d => d.Timestamp).Paginate(query));
        }

        public Task AddAsync(DepositDetails deposit) => AddOrUpdateAsync(deposit);

        public Task UpdateAsync(DepositDetails deposit) => AddOrUpdateAsync(deposit);

        private Task AddOrUpdateAsync(DepositDetails deposit)
        {
            var rlp = _rlpDecoder.Encode(deposit);
            _database.Set(deposit.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private DepositDetails Decode(byte[] bytes)
            => bytes is null
                ? null
                : _rlpDecoder.Decode(bytes.AsRlpStream());
    }
}