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
            if(depositId == null)
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
            deposit.SetConfirmationTimestamp((uint) _timestamper.UnixTime.Seconds);
            await _depositDetailsRepository.UpdateAsync(deposit);
            if (_logger.IsWarn) _logger.Warn($"NDM instantly verified deposit with id '{depositId}'.");

            return depositId;
        }
    }
}
