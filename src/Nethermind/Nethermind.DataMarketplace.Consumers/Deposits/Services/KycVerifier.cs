// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
