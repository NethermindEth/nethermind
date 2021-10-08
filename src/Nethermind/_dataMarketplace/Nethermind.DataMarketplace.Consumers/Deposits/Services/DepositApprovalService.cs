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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Notifiers;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class DepositApprovalService : IDepositApprovalService
    {
        private const int MaxKycChars = 1024 * 32;
        private readonly IDataAssetService _dataAssetService;
        private readonly IProviderService _providerService;
        private readonly IConsumerDepositApprovalRepository _depositApprovalRepository;
        private readonly ITimestamper _timestamper;
        private readonly IConsumerNotifier _consumerNotifier;
        private readonly ILogger _logger;

        public DepositApprovalService(IDataAssetService dataAssetService, IProviderService providerService,
            IConsumerDepositApprovalRepository depositApprovalRepository, ITimestamper timestamper,
            IConsumerNotifier consumerNotifier, ILogManager logManager)
        {
            _dataAssetService = dataAssetService;
            _providerService = providerService;
            _depositApprovalRepository = depositApprovalRepository;
            _timestamper = timestamper;
            _consumerNotifier = consumerNotifier;
            _logger = logManager.GetClassLogger();
        }
        
        public Task<PagedResult<DepositApproval>> BrowseAsync(GetConsumerDepositApprovals query)
            => _depositApprovalRepository.BrowseAsync(query);

        public async Task<Keccak?> RequestAsync(Keccak assetId, Address consumer, string kyc)
        {
            if (_logger.IsInfo) _logger.Info($"Requesting a deposit approval for data asset: '{assetId}', consumer: '{consumer}'...");
            var dataAsset = _dataAssetService.GetDiscovered(assetId);
            if (dataAsset is null)
            {
                if (_logger.IsError) _logger.Error($"Data asset: '{assetId}' was not found.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(kyc))
            {
                if (_logger.IsError) _logger.Error("KYC cannot be empty.");
                return null;
            }

            if (kyc.Length > MaxKycChars)
            {
                if (_logger.IsError) _logger.Error($"Invalid KYC (over {MaxKycChars} chars).");
                return null;
            }

            var providerPeer = _providerService.GetPeer(dataAsset.Provider.Address);
            if (providerPeer is null)
            {
                if (_logger.IsError) _logger.Error($"Provider for address: '{dataAsset.Provider.Address}' was not found.");
                return null;
            }

            Keccak id = DepositApproval.CalculateId(assetId, consumer);
            DepositApproval? approval = await _depositApprovalRepository.GetAsync(id);
            if (approval is null)
            {
                approval = new DepositApproval(assetId, dataAsset.Name, kyc, consumer,
                    dataAsset.Provider.Address, _timestamper.UnixTime.Seconds, DepositApprovalState.Pending);
                await _depositApprovalRepository.AddAsync(approval);
            }

            providerPeer.SendRequestDepositApproval(assetId, consumer, kyc);
            if (_logger.IsInfo) _logger.Info($"Requested a deposit approval for data asset: '{assetId}', consumer: '{consumer}'.");

            return id;
        }

        public async Task ConfirmAsync(Keccak assetId, Address consumer)
        {
            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(consumer)).Bytes);
            var approval = await _depositApprovalRepository.GetAsync(id);
            if (approval is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' was not found.");
                return;
            }

            if (approval.State == DepositApprovalState.Confirmed)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' was already confirmed.");
                return;
            }
            
            approval.Confirm();
            await _depositApprovalRepository.UpdateAsync(approval);
            await _consumerNotifier.SendDepositApprovalConfirmedAsync(approval.AssetId, approval.AssetName, consumer);
            if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' was confirmed.");
        }

        public async Task RejectAsync(Keccak assetId, Address consumer)
        {
            var id = Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(consumer)).Bytes);
            var approval = await _depositApprovalRepository.GetAsync(id);
            if (approval is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' was not found.");
                return;
            }

            if (approval.State == DepositApprovalState.Rejected)
            {
                if (_logger.IsWarn) _logger.Warn($"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' was already rejected.");
                return;
            }
            
            approval.Reject();
            await _depositApprovalRepository.UpdateAsync(approval);
            await _consumerNotifier.SendDepositApprovalRejectedAsync(approval.AssetId, approval.AssetName, consumer);
            if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{assetId}', consumer: '{consumer}' was rejected.");
        }

        public async Task UpdateAsync(IReadOnlyList<DepositApproval> approvals, Address provider)
        {
            if (!approvals.Any())
            {
                return;
            }

            if (_logger.IsInfo) _logger.Info($"Received {approvals.Count} deposit approvals from provider: '{provider}'.");
            var existingApprovals = await _depositApprovalRepository.BrowseAsync(new GetConsumerDepositApprovals
            {
                Provider = provider,
                Results = int.MaxValue
            });
            foreach (var approval in approvals)
            {
                var existingDepositApproval = existingApprovals.Items.SingleOrDefault(a => a.Id == approval.Id);
                if (existingDepositApproval is null)
                {
                    if (_logger.IsError) _logger.Error($"Provider sent an unknown deposit approval for data asset: '{approval.AssetId}', consumer: '{approval.Consumer}'.");
                    continue;
                }

                if (existingDepositApproval.State == approval.State)
                {
                    continue;
                }

                switch (approval.State)
                {
                    case DepositApprovalState.Confirmed:
                        existingDepositApproval.Confirm();
                        await _depositApprovalRepository.UpdateAsync(existingDepositApproval);
                        await _consumerNotifier.SendDepositApprovalConfirmedAsync(approval.AssetId, approval.AssetName,
                            approval.Consumer);
                        if (_logger.IsInfo) _logger.Info($"Deposit approval for data asset: '{approval.AssetId}', consumer: '{approval.Consumer}' was confirmed.");
                        break;
                    case DepositApprovalState.Rejected:
                        existingDepositApproval.Reject();
                        await _depositApprovalRepository.UpdateAsync(existingDepositApproval);
                        await _consumerNotifier.SendDepositApprovalRejectedAsync(approval.AssetId, approval.AssetName,
                            approval.Consumer);
                        if (_logger.IsWarn) _logger.Warn($"Deposit approval for data asset: '{approval.AssetId}', consumer: '{approval.Consumer}' was rejected.");
                        break;
                    case DepositApprovalState.Pending:
                        if (_logger.IsError) _logger.Error($"Deposit approval changed state back to pending after being in {existingDepositApproval.State} state: asset '{approval.AssetId}', consumer: '{approval.Consumer}' is in pending state.");
                        break;
                }
            }
        }
    }
}
