// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class InstantDepositManager : IDepositManager
    {
        private readonly IDepositManager _depositManager;
        private readonly IDepositDetailsRepository _depositDetailsRepository;
        private readonly ITimestamper _timestamper;
        private readonly uint _requiredBlockConfirmations;
        private readonly ILogger _logger;

        public InstantDepositManager(IDepositManager depositManager, IDepositDetailsRepository depositDetailsRepository,
            ITimestamper timestamper, ILogManager logManager, uint requiredBlockConfirmations)
        {
            _depositManager = depositManager;
            _depositDetailsRepository = depositDetailsRepository;
            _timestamper = timestamper;
            _logger = logManager.GetClassLogger();
            _requiredBlockConfirmations = requiredBlockConfirmations;
        }

        public Task<DepositDetails?> GetAsync(Keccak depositId) => _depositManager.GetAsync(depositId);

        public Task<PagedResult<DepositDetails>> BrowseAsync(GetDeposits query) => _depositManager.BrowseAsync(query);

        public async Task<Keccak?> MakeAsync(Keccak assetId, uint units, UInt256 value, Address address,
            UInt256? gasPrice = null)
        {
            Keccak? depositId = await _depositManager.MakeAsync(assetId, units, value, address, gasPrice);
            if (depositId == null)
            {
                return null;
            }

            if (_logger.IsWarn) _logger.Warn($"NDM instantly verifying deposit with id: '{depositId}'...");
            DepositDetails? deposit = await _depositDetailsRepository.GetAsync(depositId);
            if (deposit is null)
            {
                throw new InvalidDataException($"Deposit details are null just after creating deposit with id '{depositId}'");
            }

            if (deposit.Transaction == null)
            {
                throw new InvalidDataException($"Retrieved a deposit {depositId} without Transaction set.");
            }

            deposit.Transaction.SetIncluded();
            deposit.SetConfirmations(_requiredBlockConfirmations);
            deposit.SetConfirmationTimestamp((uint)_timestamper.UnixTime.Seconds);
            await _depositDetailsRepository.UpdateAsync(deposit);
            if (_logger.IsWarn) _logger.Warn($"NDM instantly verified deposit with id '{depositId}'.");

            return depositId;
        }
    }
}
