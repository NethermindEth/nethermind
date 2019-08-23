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

using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class DepositProvider : IDepositProvider
    {
        private readonly ConcurrentDictionary<Keccak, DepositDetails> _deposits =
            new ConcurrentDictionary<Keccak, DepositDetails>();

        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly ILogger _logger;

        public DepositProvider(IDepositDetailsRepository depositRepository,
            IConsumerSessionRepository sessionRepository, ILogManager logManager)
        {
            _depositRepository = depositRepository;
            _sessionRepository = sessionRepository;
            _logger = logManager.GetClassLogger();
        }

        public async Task<DepositDetails> GetAsync(Keccak depositId)
        {
            if (_deposits.TryGetValue(depositId, out var deposit))
            {
                return deposit;
            }

            deposit = await FetchAsync(depositId);
            if (deposit is null)
            {
                if (_logger.IsError) _logger.Error($"Deposit with id: '{depositId}' was not found.'");
                return null;
            }

            _deposits.TryAdd(depositId, deposit);

            return deposit;
        }

        private async Task<DepositDetails> FetchAsync(Keccak depositId)
        {
            var deposit = await _depositRepository.GetAsync(depositId);
            if (deposit is null)
            {
                return null;
            }

            var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = deposit.Id,
                Results = int.MaxValue
            });
            var consumedUnits = sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
            deposit.SetConsumedUnits(consumedUnits);

            return deposit;
        }
    }
}