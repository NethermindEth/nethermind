// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class DepositProvider : IDepositProvider
    {
        private readonly ConcurrentDictionary<Keccak, DepositDetails> _deposits =
            new ConcurrentDictionary<Keccak, DepositDetails>();

        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IDepositUnitsCalculator _depositUnitsCalculator;
        private readonly ILogger _logger;

        public DepositProvider(IDepositDetailsRepository depositRepository,
            IDepositUnitsCalculator depositUnitsCalculator, ILogManager logManager)
        {
            _depositRepository = depositRepository;
            _depositUnitsCalculator = depositUnitsCalculator;
            _logger = logManager.GetClassLogger();
        }

        public async Task<DepositDetails?> GetAsync(Keccak depositId)
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

        private async Task<DepositDetails?> FetchAsync(Keccak depositId)
        {
            DepositDetails? deposit = await _depositRepository.GetAsync(depositId);
            if (deposit is null)
            {
                return null;
            }

            uint consumedUnits = await _depositUnitsCalculator.GetConsumedAsync(deposit);
            deposit.SetConsumedUnits(consumedUnits);

            return deposit;
        }
    }
}
