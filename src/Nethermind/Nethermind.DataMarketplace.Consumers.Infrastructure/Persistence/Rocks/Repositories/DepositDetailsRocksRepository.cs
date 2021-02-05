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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories
{
    public class DepositDetailsRocksRepository : IDepositDetailsRepository
    {
        private readonly IDb _database;
        private readonly IRlpNdmDecoder<DepositDetails> _rlpDecoder;
        private readonly IDepositUnitsCalculator _depositUnitsCalculator;

        public DepositDetailsRocksRepository(IDb database, IRlpNdmDecoder<DepositDetails> rlpDecoder, IDepositUnitsCalculator depositUnitsCalculator)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
            _depositUnitsCalculator = depositUnitsCalculator;
        }

        public async Task<DepositDetails?> GetAsync(Keccak id)
        {
            await Task.CompletedTask;

            byte[] bytes = _database.Get(id);
            if (bytes == null)
            {
                return null;
            }
            
            return Decode(bytes);
        }

        public async Task<PagedResult<DepositDetails>> BrowseAsync(GetDeposits query)
        {
            if (query is null)
            {
                throw new ArgumentNullException(nameof(query));
            }
            
            var depositsBytes = _database.GetAllValues().ToArray();
            if (depositsBytes.Length == 0)
            {
                return PagedResult<DepositDetails>.Empty;
            }

            DepositDetails[] deposits = new DepositDetails[depositsBytes.Length];
            for (var i = 0; i < depositsBytes.Length; i++)
            {
                deposits[i] = Decode(depositsBytes[i]);
            }

            IEnumerable<DepositDetails> filteredDeposits = deposits.AsEnumerable();
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
                
                filteredDeposits = GetEligibleToRefund(filteredDeposits, query.CurrentBlockTimestamp);
            }

            return filteredDeposits.OrderByDescending(d => d.Timestamp).ToArray().Paginate(query);
        }

        public Task AddAsync(DepositDetails deposit) => AddOrUpdateAsync(deposit);

        public Task UpdateAsync(DepositDetails deposit) => AddOrUpdateAsync(deposit);

        private Task AddOrUpdateAsync(DepositDetails deposit)
        {
            Serialization.Rlp.Rlp rlp = _rlpDecoder.Encode(deposit);
            _database.Set(deposit.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private DepositDetails Decode(byte[] bytes)
            => _rlpDecoder.Decode(bytes.AsRlpStream());

        private IEnumerable<DepositDetails> GetEligibleToRefund(IEnumerable<DepositDetails> deposits, long currentTimestamp)
        {
            bool refundClaimed;
            bool notConsumedAllUnits;
            bool isTimeType;
            bool hasEarlyTicket;
            bool isExpired;

            foreach(var deposit in deposits)
            {
                refundClaimed = deposit.RefundClaimed;
                notConsumedAllUnits = deposit.ConsumedUnits < deposit.Deposit.Units;
                isTimeType = deposit.DataAsset.UnitType == DataAssetUnitType.Time; 
                hasEarlyTicket = !(deposit.EarlyRefundTicket is null);
                isExpired = currentTimestamp >= deposit.Deposit.ExpiryTime;

                // in time deposits, during the refund: units consumed will be always equal to all deposit units, hence we do not check whether all units are consumed
                if(!refundClaimed && (notConsumedAllUnits || isTimeType) && (hasEarlyTicket || isExpired))
                {
                    yield return deposit;
                }
            }
        }
    }
}
