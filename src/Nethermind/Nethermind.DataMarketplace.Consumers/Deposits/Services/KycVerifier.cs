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

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class KycVerifier : IKycVerifier
    {
        private readonly IConsumerDepositApprovalRepository _depositApprovalRepository;
        private readonly ILogger _logger;

        public KycVerifier(IConsumerDepositApprovalRepository depositApprovalRepository, ILogManager logManager)
        {
            _depositApprovalRepository = depositApprovalRepository;
            _logger = logManager.GetClassLogger();
        }
    
        public async Task<bool> IsVerifiedAsync(Keccak dataAssetId, Address address)
        {
            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(dataAssetId), Rlp.Encode(address)).Bytes);
            var depositApproval = await _depositApprovalRepository.GetAsync(id);
            if (depositApproval is null)
            {
                if (_logger.IsError) _logger.Error($"Deposit approval for data asset: '{dataAssetId}' was not found.");

                return false;
            }

            if (depositApproval.State != DepositApprovalState.Confirmed)
            {
                if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{dataAssetId}' has state: '{depositApproval.State}'.");

                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{dataAssetId}' was confirmed, required KYC is valid.");

            return true;
        }
    }
}